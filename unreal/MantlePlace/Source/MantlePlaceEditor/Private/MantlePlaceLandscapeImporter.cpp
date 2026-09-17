// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceLandscapeImporter.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
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
#include "HAL/PlatformTime.h"
#include "Misc/FileHelper.h"
#include "Misc/ScopedSlowTask.h"
#include "Modules/ModuleManager.h"
#include "RenderingThread.h"  // FlushRenderingCommands
#include "ShaderCompiler.h"   // GShaderCompilingManager
#include "UObject/Package.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceLandscape, Log, All);

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
	 * One ULandscapeLayerInfoObject per material, created in its own package and marked dirty --
	 * never saved, like everything else this importer generates. Nothing in this plugin created one
	 * before, so this is where a weight plane becomes something the engine (and the Landscape
	 * editor, and PCG's layer sampling) can address by name. Idempotent by load-first, like the
	 * drape MIC: a re-import of the same bundle reuses the asset rather than colliding on the
	 * object name.
	 */
	static ULandscapeLayerInfoObject* GetOrCreateLayerInfo(
		const FString& Material, const FString& DestPackagePath)
	{
		const FString AssetName = MantlePlaceImportNaming::LayerInfoName(Material);
		const FString PackageName =
			MantlePlaceImportNaming::SubfolderPath(
				DestPackagePath, MantlePlaceImportNaming::ESubfolder::Landcover)
			/ AssetName;
		const FString ObjectPath = MantlePlaceImportNaming::ObjectPathOf(PackageName, AssetName);

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

		// Every phase below is FScopedCurrentPhase rather than FScopedPhase, for the reason that form
		// exists: this is a different translation unit from the one that owns the import's timeline,
		// and threading a timing parameter through a signature that has nothing else to do with
		// timing is what the current-import lookup replaces. Each is null-safe — this importer is
		// also driven by tests, where no import is running and every one of them records nothing.
		TArray<uint16> Samples;
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase DecodePhase(
				MantlePlaceImportTiming::Phase::LandscapeHeightmapDecode);
			if (!DecodeHeightmapPng(HeightmapFile, SizeX, Samples, OutError))
			{
				return nullptr;
			}
		}

		// Orient so North maps to +X and East to +Y — Unreal's LEFT-handed world frame. This is a
		// TRANSPOSE, not a row flip: the PNG is a map raster whose rows run north->south and whose
		// columns run west->east, so the landscape's local X (north) indexes the PNG's ROWS and its
		// local Y (east) indexes the PNG's COLUMNS. Copying rows straight across instead — the way
		// this read before — puts East on +X, which swaps two axes of a Z-up frame. That is a
		// reflection (determinant -1), not a rotation, so it mirrored every imported bundle across
		// the NE diagonal and read as a 90-degree rotation on a near-square AOI.
		//
		// X is the engine's inner/fast axis (HeightData index = X + Y*SizeX), so the source stride
		// is the one that walks: SrcRow advances down the PNG as X advances north.
		const int32 SrcWidth = SizeY;  // PNG columns  -> landscape Y (east)
		const int32 SrcHeight = SizeX; // PNG rows     -> landscape X (north)
		TArray<uint16> HeightData;
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase OrientPhase(
				MantlePlaceImportTiming::Phase::LandscapeHeightmapOrient);
			HeightData.SetNumUninitialized(SizeX * SizeY);
			for (int32 Y = 0; Y < SizeY; ++Y)
			{
				for (int32 X = 0; X < SizeX; ++X)
				{
					// Landscape X=0 is the south edge; PNG row 0 is the north edge when the bundle says so.
					const int32 SrcRow = Manifest.bRow0IsNorth ? (SrcHeight - 1 - X) : X;
					HeightData[X + static_cast<int64>(Y) * SizeX] =
						Samples[static_cast<int64>(SrcRow) * SrcWidth + Y];
				}
			}
		}

		const FVector Scale = Manifest.GetLandscapeScale();
		const FVector SpawnLocation = Manifest.GetLandscapeSpawnLocation();

		ALandscape* Landscape = nullptr;
		{
			// The spawn and the three property assignments that have to precede Import(), together:
			// they are one step as far as ordering goes, and splitting them would produce three rows
			// nobody can act on separately.
			const MantlePlaceImportTiming::FScopedCurrentPhase SpawnPhase(
				MantlePlaceImportTiming::Phase::LandscapeActorSpawn);

			Landscape = World->SpawnActor<ALandscape>(SpawnLocation, FRotator::ZeroRotator);
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
		}

		TMap<FGuid, TArray<uint16>> HeightDataPerLayers;
		HeightDataPerLayers.Add(FGuid(), MoveTemp(HeightData));

		// The material-weight layers the bundle published. A plane whose layer-info asset could not
		// be created is dropped rather than passed with a null LayerInfo — the engine treats those as
		// unresolved and they would silently paint nothing.
		TArray<FLandscapeImportLayerInfo> ImportLayers;
		ImportLayers.Reserve(WeightPlanes.Num());
		{
			// Package creation and asset registration per paint layer, plus the copy of each plane's
			// samples into the engine's own structure. Both are per-layer costs that scale with the
			// bundle's legend, which is what makes them worth a row of their own.
			const MantlePlaceImportTiming::FScopedCurrentPhase LayerInfoPhase(
				MantlePlaceImportTiming::Phase::LandscapeLayerInfoAssets);
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
		}

		TMap<FGuid, TArray<FLandscapeImportLayerInfo>> MaterialLayerDataPerLayers;
		MaterialLayerDataPerLayers.Add(FGuid(), MoveTemp(ImportLayers));

		{
			// ⚠ The floor of this breakdown. Heightmap upload, component construction, weightmap
			// application and collision all happen inside this one engine call, and none of them can
			// be given a row from here — `ALandscape::Import` is engine code. A row that says "the
			// engine's own import cost N seconds" is the most this plugin can honestly report, and
			// splitting it further needs Unreal Insights or an engine build rather than another
			// phase name.
			const MantlePlaceImportTiming::FScopedCurrentPhase EngineImportPhase(
				MantlePlaceImportTiming::Phase::LandscapeEngineImport);
			Landscape->Import(
				FGuid::NewGuid(),
				0, 0, SizeX - 1, SizeY - 1,
				Manifest.SectionsPerComponent, Manifest.SectionSizeQuads,
				HeightDataPerLayers, *HeightmapFile,
				MaterialLayerDataPerLayers, ELandscapeImportAlphamapType::Additive,
				TArrayView<const FLandscapeLayer>());
		}

		{
			const MantlePlaceImportTiming::FScopedCurrentPhase LayerInfoMapPhase(
				MantlePlaceImportTiming::Phase::LandscapeLayerInfoMap);
			if (ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
			{
				Info->UpdateLayerInfoMap(Landscape);
			}
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
		//
		// This blocks, and it is the longest thing an import does. It stays INSIDE the import
		// transaction, and the reasoning above is why: the whole sequence exists because the scene
		// proxy snapshots component materials at construction, so the finish, the rebuild and the
		// flush have to happen between Import() building those components and control returning to
		// the importer. Moving any of the three outside the transaction moves it after the actor
		// operations that follow, which is the ordering the "didn't take" attempt already
		// demonstrated does not work. So the second option in the report is taken instead: it is
		// never a silent freeze.
		//
		// The progress is not decoration, and all three steps now report into the running import's
		// timeline as well as onto the log line below. That is a change from how this read when it
		// was written: only the stall was reported, on the grounds that it was the one with a
		// hypothesis attached — which left two thirds of a cost that had been measured all along
		// outside the summary block, visible to a person reading a log and to nothing else.
		if (DrapeMaterial != nullptr)
		{
			FScopedSlowTask ShaderTask(3.0f, NSLOCTEXT("MantlePlaceImporter", "CompilingLandscapeShaders",
				"Mantle Place: compiling landscape material shaders..."));
			ShaderTask.MakeDialog();

			// 1) Block until the landscape combination shaders kicked off by Import() have compiled.
			ShaderTask.EnterProgressFrame(1.0f, NSLOCTEXT("MantlePlaceImporter", "FinishingShaderCompilation",
				"Mantle Place: waiting for landscape shaders to finish compiling..."));
			const double CompileStart = FPlatformTime::Seconds();
			if (GShaderCompilingManager != nullptr)
			{
				UE_LOG(LogMantlePlaceLandscape, Log,
					TEXT("Waiting for the landscape's combination shaders to finish compiling before "
						 "rebuilding its material instances. The editor is blocked until they do; the "
						 "line that follows says for how long."));
				GShaderCompilingManager->FinishAllCompilation();
			}
			const double CompileSeconds = FPlatformTime::Seconds() - CompileStart;

			// The same number the line below already prints, reported into the running import's
			// timeline so it appears in the summary block beside every other phase. It is measured
			// here rather than wrapped in a guard because the measurement already existed — all
			// three steps have been timed since the day this sequence was written, and a second
			// clock over the same span would let the log line and the timeline drift. The rebuild
			// and the flush below report the same way, for the same reason. Null-safe by
			// construction: this importer is also driven by tests, where there is no import and
			// RecordOnCurrent does nothing.
			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::ShaderStall, CompileSeconds);

			// 2) Rebuild the combination + per-component material instances from a clean slate (true
			//    invalidates the cached combination map) and recreate render state for every component.
			ShaderTask.EnterProgressFrame(1.0f, NSLOCTEXT("MantlePlaceImporter", "RebuildingMaterialInstances",
				"Mantle Place: rebuilding landscape material instances..."));
			const double RebuildStart = FPlatformTime::Seconds();
			Landscape->UpdateAllComponentMaterialInstances(/*bInInvalidateCombinationMaterials*/ true);
			const double RebuildSeconds = FPlatformTime::Seconds() - RebuildStart;
			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::LandscapeMaterialRebuild, RebuildSeconds);

			// 3) Let the render thread apply the recreated proxies before we hand control back.
			ShaderTask.EnterProgressFrame(1.0f, NSLOCTEXT("MantlePlaceImporter", "FlushingRenderCommands",
				"Mantle Place: applying the drape..."));
			const double FlushStart = FPlatformTime::Seconds();
			FlushRenderingCommands();
			const double FlushSeconds = FPlatformTime::Seconds() - FlushStart;
			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::LandscapeRenderFlush, FlushSeconds);

			// One line, all three numbers, so the share of import time this accounts for is a fact
			// somebody can read off a log rather than a thing to guess at.
			UE_LOG(LogMantlePlaceLandscape, Log,
				TEXT("Landscape drape ready: shader compilation %.2fs, material instance rebuild %.2fs, "
					 "render flush %.2fs (%.2fs total)."),
				CompileSeconds, RebuildSeconds, FlushSeconds,
				CompileSeconds + RebuildSeconds + FlushSeconds);
		}

		// Not labelled here. Every actor an import creates is labelled, tagged and filed into its
		// outliner folder by one function in MantlePlaceImporterLibrary, so a new actor type cannot
		// be added and forget one of the three.
		return Landscape;
	}
}
