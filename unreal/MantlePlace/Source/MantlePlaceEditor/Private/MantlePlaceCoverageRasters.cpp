// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceCoverageRasters.h"

#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceCoverageRasterTypes.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"

#include "AssetToolsModule.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/Texture2D.h"
#include "IAssetTools.h"
#include "AssetImportTask.h"
#include "UObject/Package.h"

namespace MantlePlaceCoverageRasters
{
	UTexture2D* ImportRaster(
		const FMantlePlaceLandscapeLayer& Layer,
		const FMantlePlaceUeReadyRaster& Raster,
		const FString& DiskFile,
		const FString& JobId,
		const FString& DestPackagePath,
		FString& OutError)
	{
		EMantlePlaceCoverageMapping Kind = EMantlePlaceCoverageMapping::Identity;
		if (!FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
				Raster.Encoding, Raster.ValueMapping, Kind, OutError))
		{
			return nullptr;
		}

		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = DiskFile;
		Task->DestinationPath = MantlePlaceImportNaming::SubfolderPath(
			DestPackagePath, MantlePlaceImportNaming::ESubfolder::CoverageRasters);
		Task->DestinationName = MantlePlaceImportNaming::TextureName(DiskFile);
		Task->bAutomated = true;
		Task->bReplaceExisting = true;
		Task->bSave = false;

		TArray<UAssetImportTask*> Tasks;
		Tasks.Add(Task);
		Module.Get().ImportAssetTasks(Tasks);

		UTexture2D* Texture = nullptr;
		for (const FString& Path : Task->ImportedObjectPaths)
		{
			Texture = LoadObject<UTexture2D>(nullptr, *Path);
			if (Texture != nullptr)
			{
				break;
			}
		}
		if (Texture == nullptr)
		{
			OutError = FString::Printf(TEXT("Could not import %s as a texture."), *Raster.Path);
			return nullptr;
		}

		FMantlePlaceCoverageTextureSettings Settings;
		if (!FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
				Raster.Encoding, Texture->Source.GetFormat(), Settings, OutError))
		{
			Texture->MarkAsGarbage();
			return nullptr;
		}

		Texture->CompressionSettings = Settings.Compression;
		Texture->SRGB = Settings.bSRGB;
		Texture->MipGenSettings = Settings.MipGen;
		Texture->Filter = Settings.Filter;
		Texture->VirtualTextureStreaming = Settings.bVirtualTextureStreaming;

		Texture->AddressX = TA_Clamp;
		Texture->AddressY = TA_Clamp;
		Texture->PostEditChange();

		UMantlePlaceCoverageRasterData* Data = NewObject<UMantlePlaceCoverageRasterData>(Texture);
		Data->Mapping = Kind;
		Data->LayerName = Layer.Name;
		Data->Encoding = Raster.Encoding;
		Data->Orientation = EMantlePlaceCoverageOrientation::NorthUpRowMajor;
		Data->MinValue = Raster.ValueMapping.MinValue;
		Data->MaxValue = Raster.ValueMapping.MaxValue;
		Data->ToValueFormula = Raster.ValueMapping.ToValueFormula;
		Data->Units = Raster.ValueMapping.Units;
		Data->bHasNodata = Raster.ValueMapping.bHasNodata;
		Data->NodataValue = Raster.ValueMapping.NodataValue;
		Data->Classes = Raster.ValueMapping.Classes;
		Data->TrueValue = Raster.ValueMapping.TrueValue;
		Data->FalseValue = Raster.ValueMapping.FalseValue;
		Data->SourcePath = Raster.Path;
		Data->Sha256 = Raster.Sha256;
		Data->JobId = JobId;
		Texture->AddAssetUserData(Data);

		Texture->MarkPackageDirty();
		return Texture;
	}
}
