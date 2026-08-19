// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceLandscapeImporter.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLandscapeWeightsLogic.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/World.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Landscape.h"
#include "LandscapeProxy.h"
#include "LandscapeInfo.h"
#include "LandscapeLayerInfoObject.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Misc/FileHelper.h"
#include "Modules/ModuleManager.h"
#include "RenderingThread.h"  // FlushRenderingCommands
#include "ShaderCompiler.h"   // GShaderCompilingManager
#include "UObject/Package.h"

namespace MantlePlaceLandscapeImporter
{
	/** Decode a 16-bit grayscale PNG into Width*Height host-endian uint16 samples. */
	static bool DecodeHeightmapPng(const FString& File, int32 ExpectedSize, TArray<uint16>& OutSamples, FString& OutError)
	{
		TArray<uint8> Compressed;
		if (!FFileHelper::LoadFileToArray(Compressed, *File))
		{
			OutError = FString::Printf(TEXT("Could not read heightmap file: %s"), *File);
			return false;
		}

		IImageWrapperModule& Module = FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
		const TSharedPtr<IImageWrapper> Wrapper = Module.CreateImageWrapper(EImageFormat::PNG);
		if (!Wrapper.IsValid() || !Wrapper->SetCompressed(Compressed.GetData(), Compressed.Num()))
		{
			OutError = TEXT("Heightmap is not a readable PNG.");
			return false;
		}

		const int32 Width = Wrapper->GetWidth();
		const int32 Height = Wrapper->GetHeight();
		if (Width != ExpectedSize || Height != ExpectedSize)
		{
			OutError = FString::Printf(
				TEXT("Heightmap is %dx%d but the manifest expects %dx%d."), Width, Height, ExpectedSize, ExpectedSize);
			return false;
		}

		TArray64<uint8> Raw;
		if (!Wrapper->GetRaw(ERGBFormat::Gray, 16, Raw) || Raw.Num() != static_cast<int64>(Width) * Height * 2)
		{
			OutError = TEXT("Heightmap could not be decoded as 16-bit grayscale.");
			return false;
		}

		OutSamples.SetNumUninitialized(Width * Height);
		FMemory::Memcpy(OutSamples.GetData(), Raw.GetData(), Raw.Num());
		return true;
	}

	bool DecodeRgbaPng(
		const FString& File,
		const FString& InZipPath,
		FMantlePlaceRgbaImage& OutImage,
		FString& OutError)
	{
		TArray<uint8> Compressed;
		if (!FFileHelper::LoadFileToArray(Compressed, *File))
		{
			OutError = FString::Printf(TEXT("Could not read %s"), *File);
			return false;
		}

		IImageWrapperModule& Module = FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
		const TSharedPtr<IImageWrapper> Wrapper = Module.CreateImageWrapper(EImageFormat::PNG);
		if (!Wrapper.IsValid() || !Wrapper->SetCompressed(Compressed.GetData(), Compressed.Num()))
		{
			OutError = FString::Printf(TEXT("%s is not a readable PNG."), *InZipPath);
			return false;
		}

		TArray64<uint8> Raw;
		if (!Wrapper->GetRaw(ERGBFormat::RGBA, 8, Raw))
		{
			OutError = FString::Printf(TEXT("%s could not be decoded as 8-bit RGBA."), *InZipPath);
			return false;
		}

		OutImage.Path = InZipPath;
		OutImage.Width = Wrapper->GetWidth();
		OutImage.Height = Wrapper->GetHeight();
		OutImage.Pixels.SetNumUninitialized(static_cast<int32>(Raw.Num()));
		FMemory::Memcpy(OutImage.Pixels.GetData(), Raw.GetData(), Raw.Num());
		return true;
	}

	/**
	 * One saved ULandscapeLayerInfoObject per material. Nothing in this plugin created one before, so
	 * this is where a weight plane becomes something the engine (and the Landscape editor, and PCG's
	 * layer sampling) can address by name. Idempotent by load-first, like the drape MIC: a re-import
	 * of the same bundle reuses the asset rather than colliding on the object name.
	 */
	static ULandscapeLayerInfoObject* GetOrCreateLayerInfo(
		const FString& Material, const FString& DestPackagePath)
	{
		const FString AssetName = FString::Printf(TEXT("LI_%s"), *Material);
		const FString PackageName = DestPackagePath / TEXT("Landcover") / AssetName;
		const FString ObjectPath = FString::Printf(TEXT("%s.%s"), *PackageName, *AssetName);

		if (ULandscapeLayerInfoObject* Existing = LoadObject<ULandscapeLayerInfoObject>(nullptr, *ObjectPath))
		{
			return Existing;
		}

		UPackage* Package = CreatePackage(*PackageName);
		if (Package == nullptr)
		{
			return nullptr;
		}
		ULandscapeLayerInfoObject* LayerInfo = NewObject<ULandscapeLayerInfoObject>(
			Package, FName(*AssetName), RF_Public | RF_Standalone | RF_Transactional);
		if (LayerInfo == nullptr)
		{
			return nullptr;
		}

		// The layer NAME is what a landscape material's LandscapeLayerBlend nodes bind to, so it is
		// the ETL's material name verbatim (HPS-33) — never a prettified or prefixed variant.
		LayerInfo->SetLayerName(FName(*Material), /*bInModify*/ false);
		LayerInfo->SetLayerUsageDebugColor(
			LayerInfo->GenerateLayerUsageDebugColor(), /*bInModify*/ false, EPropertyChangeType::ValueSet);

		FAssetRegistryModule::AssetCreated(LayerInfo);
		Package->MarkPackageDirty();
		return LayerInfo;
	}

	ALandscape* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& HeightmapFile,
		UMaterialInstanceConstant* DrapeMaterial,
		const TArray<FMantlePlaceWeightPlane>& WeightPlanes,
		const FString& DestPackagePath,
		FString& OutError)
	{
		if (World == nullptr)
		{
			OutError = TEXT("No editor world to import the landscape into.");
			return nullptr;
		}

		const int32 SizeX = Manifest.Resolution;
		const int32 SizeY = Manifest.Resolution;

		TArray<uint16> Samples;
		if (!DecodeHeightmapPng(HeightmapFile, SizeX, Samples, OutError))
		{
			return nullptr;
		}

		// Orient so North maps to +Y. The Landscape's row Y=0 is the south (corner) edge, while
		// the PNG's row 0 is North when row0_is_north — so flip vertically. Columns (X) are
		// West->East in both. HeightData index = X + Y*SizeX (X is the inner/fast axis).
		TArray<uint16> HeightData;
		HeightData.SetNumUninitialized(SizeX * SizeY);
		for (int32 Y = 0; Y < SizeY; ++Y)
		{
			const int32 SrcRow = Manifest.bRow0IsNorth ? (SizeY - 1 - Y) : Y;
			FMemory::Memcpy(
				HeightData.GetData() + static_cast<int64>(Y) * SizeX,
				Samples.GetData() + static_cast<int64>(SrcRow) * SizeX,
				static_cast<int64>(SizeX) * sizeof(uint16));
		}

		const FVector Scale = Manifest.GetLandscapeScale();
		const FVector SpawnLocation = Manifest.GetLandscapeSpawnLocation();

		ALandscape* Landscape = World->SpawnActor<ALandscape>(SpawnLocation, FRotator::ZeroRotator);
		if (Landscape == nullptr)
		{
			OutError = TEXT("Failed to spawn the Landscape actor.");
			return nullptr;
		}

		// Match the engine's New-Landscape path: set scale before Import, leave edit-layer
		// capability at its default (the FGuid() height-data key is the correct no-edit-layer import).
		Landscape->SetActorRelativeScale3D(Scale);

		// Assign the drape material BEFORE Import() so the per-component material instances are built
		// with it from the start. A landscape assigned a material *after* creation keeps rendering the
		// default material (the components aren't rebuilt) until the assignment is re-applied to a
		// finalized landscape — so the material has to be in hand here, before the components exist.
		if (DrapeMaterial != nullptr)
		{
			Landscape->LandscapeMaterial = DrapeMaterial;
		}

		Landscape->StaticLightingLOD =
			FMath::DivideAndRoundUp(FMath::CeilLogTwo((SizeX * SizeY) / (2048 * 2048) + 1), static_cast<uint32>(2));

		TMap<FGuid, TArray<uint16>> HeightDataPerLayers;
		HeightDataPerLayers.Add(FGuid(), MoveTemp(HeightData));

		// The material-weight layers the bundle published. A plane whose layer-info asset could not
		// be created is dropped rather than passed with a null LayerInfo — the engine treats those as
		// unresolved and they would silently paint nothing.
		TArray<FLandscapeImportLayerInfo> ImportLayers;
		ImportLayers.Reserve(WeightPlanes.Num());
		for (const FMantlePlaceWeightPlane& Plane : WeightPlanes)
		{
			if (Plane.Data.Num() != SizeX * SizeY)
			{
				OutError = FString::Printf(
					TEXT("Weight layer \"%s\" is %d samples but the landscape is %dx%d."),
					*Plane.Material, Plane.Data.Num(), SizeX, SizeY);
				return nullptr;
			}
			ULandscapeLayerInfoObject* LayerInfo = GetOrCreateLayerInfo(Plane.Material, DestPackagePath);
			if (LayerInfo == nullptr)
			{
				continue;
			}
			FLandscapeImportLayerInfo Entry(FName(*Plane.Material));
			Entry.LayerInfo = LayerInfo;
			Entry.LayerData = Plane.Data;
			ImportLayers.Add(MoveTemp(Entry));
		}

		TMap<FGuid, TArray<FLandscapeImportLayerInfo>> MaterialLayerDataPerLayers;
		MaterialLayerDataPerLayers.Add(FGuid(), MoveTemp(ImportLayers));

		Landscape->Import(
			FGuid::NewGuid(),
			0, 0, SizeX - 1, SizeY - 1,
			Manifest.SectionsPerComponent, Manifest.SectionSizeQuads,
			HeightDataPerLayers, *HeightmapFile,
			MaterialLayerDataPerLayers, ELandscapeImportAlphamapType::Additive,
			TArrayView<const FLandscapeLayer>());

		if (ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			Info->UpdateLayerInfoMap(Landscape);
		}

		// Make the drape render on the import frame — no manual "re-apply material" / level refresh.
		//
		// Import() already built each component's combination material instance from LandscapeMaterial
		// (set above, before Import) and recreated render state. BUT it does so while those landscape
		// combination shaders are still compiling asynchronously, and FLandscapeComponentSceneProxy
		// snapshots component materials once at construction — substituting the engine default for any
		// entry whose shader map isn't ready yet. So the surface shows the default material until a
		// *fresh* render-state recreate happens after the shaders finish (exactly what the manual
		// re-apply or a level reload was triggering). A prior post-Import PostEditChangeProperty attempt
		// "didn't take" for the same reason: it recreated render state still before the shaders were
		// ready. The fix is to finish the shaders first, THEN rebuild + recreate.
		if (DrapeMaterial != nullptr)
		{
			// 1) Block until the landscape combination shaders kicked off by Import() have compiled.
			if (GShaderCompilingManager != nullptr)
			{
				GShaderCompilingManager->FinishAllCompilation();
			}
			// 2) Rebuild the combination + per-component material instances from a clean slate (true
			//    invalidates the cached combination map) and recreate render state for every component.
			Landscape->UpdateAllComponentMaterialInstances(/*bInInvalidateCombinationMaterials*/ true);
			// 3) Let the render thread apply the recreated proxies before we hand control back.
			FlushRenderingCommands();
		}

		Landscape->SetActorLabel(FString::Printf(TEXT("MP_Landscape_%s"), *Manifest.JobId.Left(8)));
		return Landscape;
	}
}
