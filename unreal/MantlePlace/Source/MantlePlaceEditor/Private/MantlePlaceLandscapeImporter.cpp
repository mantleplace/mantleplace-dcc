// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLandscapeImporter.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceEditorSettings.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/World.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Landscape.h"
#include "LandscapeProxy.h"
#include "LandscapeInfo.h"
#include "LandscapeLayerInfoObject.h"
#include "LandscapeSubsystem.h"
#include "Materials/MaterialInstanceConstant.h"
#include "HAL/PlatformTime.h"
#include "Misc/FileHelper.h"
#include "Misc/ScopedSlowTask.h"
#include "Modules/ModuleManager.h"
#include "RenderingThread.h"
#include "ShaderCompiler.h"
#include "UObject/Package.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceLandscape, Log, All);

namespace MantlePlaceLandscapeImporter
{
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

	bool SplitForWorldPartition(ALandscape* Landscape, int32 GridSizeInComponents)
	{
		if (Landscape == nullptr || GridSizeInComponents <= 0)
		{
			return false;
		}
		UWorld* World = Landscape->GetWorld();
		ULandscapeSubsystem* Subsystem = World != nullptr ? World->GetSubsystem<ULandscapeSubsystem>() : nullptr;
		ULandscapeInfo* Info = Landscape->GetLandscapeInfo();
		if (Subsystem == nullptr || Info == nullptr || !Subsystem->IsGridBased())
		{
			return false;
		}
		const MantlePlaceImportTiming::FScopedCurrentPhase SplitPhase(
			MantlePlaceImportTiming::Phase::LandscapeWorldPartitionSplit);
		Subsystem->ChangeGridSize(Info, static_cast<uint32>(GridSizeInComponents));
		UE_LOG(LogMantlePlaceLandscape, Log,
			TEXT("World Partition: landscape split into %d streaming prox(ies) of %d component(s) per axis."),
			Info->GetSortedStreamingProxies().Num(), GridSizeInComponents);
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

		LayerInfo->SetLayerName(FName(*Material), false);
		LayerInfo->SetLayerUsageDebugColor(
			LayerInfo->GenerateLayerUsageDebugColor(), false, EPropertyChangeType::ValueSet);

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

		const int32 SizeX = Manifest.Heightmap.Resolution;
		const int32 SizeY = Manifest.Heightmap.Resolution;

		TArray<uint16> Samples;
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase DecodePhase(
				MantlePlaceImportTiming::Phase::LandscapeHeightmapDecode);
			if (!DecodeHeightmapPng(HeightmapFile, SizeX, Samples, OutError))
			{
				return nullptr;
			}
		}

		const int32 SrcWidth = SizeY;
		const int32 SrcHeight = SizeX;
		TArray<uint16> HeightData;
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase OrientPhase(
				MantlePlaceImportTiming::Phase::LandscapeHeightmapOrient);
			HeightData.SetNumUninitialized(SizeX * SizeY);
			for (int32 Y = 0; Y < SizeY; ++Y)
			{
				for (int32 X = 0; X < SizeX; ++X)
				{
					const int32 SrcRow = Manifest.Heightmap.bRow0IsNorth ? (SrcHeight - 1 - X) : X;
					HeightData[X + static_cast<int64>(Y) * SizeX] =
						Samples[static_cast<int64>(SrcRow) * SrcWidth + Y];
				}
			}
		}

		const FVector Scale = Manifest.GetLandscapeScale();
		const FVector SpawnLocation = Manifest.GetLandscapeSpawnLocation();

		ALandscape* Landscape = nullptr;
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase SpawnPhase(
				MantlePlaceImportTiming::Phase::LandscapeActorSpawn);

			Landscape = World->SpawnActor<ALandscape>(SpawnLocation, FRotator::ZeroRotator);
			if (Landscape == nullptr)
			{
				OutError = TEXT("Failed to spawn the Landscape actor.");
				return nullptr;
			}

			Landscape->SetActorRelativeScale3D(Scale);

			if (DrapeMaterial != nullptr)
			{
				Landscape->LandscapeMaterial = DrapeMaterial;
			}

			Landscape->StaticLightingLOD =
				FMath::DivideAndRoundUp(FMath::CeilLogTwo((SizeX * SizeY) / (2048 * 2048) + 1), static_cast<uint32>(2));
		}

		TMap<FGuid, TArray<uint16>> HeightDataPerLayers;
		HeightDataPerLayers.Add(FGuid(), MoveTemp(HeightData));

		TArray<FLandscapeImportLayerInfo> ImportLayers;
		ImportLayers.Reserve(WeightPlanes.Num());
		{
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
			const MantlePlaceImportTiming::FScopedCurrentPhase EngineImportPhase(
				MantlePlaceImportTiming::Phase::LandscapeEngineImport);
			Landscape->Import(
				FGuid::NewGuid(),
				0, 0, SizeX - 1, SizeY - 1,
				Manifest.Heightmap.SectionsPerComponent, Manifest.Heightmap.SectionSizeQuads,
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

		const bool bSplit = SplitForWorldPartition(
			Landscape, UMantlePlaceEditorSettings::ResolveWorldPartitionGridSize());

		if (DrapeMaterial != nullptr)
		{
			FScopedSlowTask ShaderTask(3.0f, NSLOCTEXT("MantlePlaceImporter", "CompilingLandscapeShaders",
				"Mantle Place: compiling landscape material shaders..."));
			ShaderTask.MakeDialog();

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

			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::ShaderStall, CompileSeconds);

			ShaderTask.EnterProgressFrame(1.0f, NSLOCTEXT("MantlePlaceImporter", "RebuildingMaterialInstances",
				"Mantle Place: rebuilding landscape material instances..."));
			const double RebuildStart = FPlatformTime::Seconds();
			ULandscapeInfo* SplitInfo = bSplit ? Landscape->GetLandscapeInfo() : nullptr;
			if (SplitInfo != nullptr)
			{
				SplitInfo->UpdateAllComponentMaterialInstances(true);
			}
			else
			{
				Landscape->UpdateAllComponentMaterialInstances(true);
			}
			const double RebuildSeconds = FPlatformTime::Seconds() - RebuildStart;
			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::LandscapeMaterialRebuild, RebuildSeconds);

			ShaderTask.EnterProgressFrame(1.0f, NSLOCTEXT("MantlePlaceImporter", "FlushingRenderCommands",
				"Mantle Place: applying the drape..."));
			const double FlushStart = FPlatformTime::Seconds();
			FlushRenderingCommands();
			const double FlushSeconds = FPlatformTime::Seconds() - FlushStart;
			MantlePlaceImportTiming::RecordOnCurrent(
				MantlePlaceImportTiming::Phase::LandscapeRenderFlush, FlushSeconds);

			UE_LOG(LogMantlePlaceLandscape, Log,
				TEXT("Landscape drape ready: shader compilation %.2fs, material instance rebuild %.2fs, "
					 "render flush %.2fs (%.2fs total)."),
				CompileSeconds, RebuildSeconds, FlushSeconds,
				CompileSeconds + RebuildSeconds + FlushSeconds);
		}

		return Landscape;
	}
}
