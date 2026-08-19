// Copyright Mantle Place. All Rights Reserved.

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
		// Resolve the mapping BEFORE importing. A raster whose value_mapping cannot be read has no
		// meaning to attach, and a texture with no meaning is the anonymous ramp this whole path
		// exists to avoid — so it is skipped rather than imported bare.
		EMantlePlaceCoverageMapping Kind = EMantlePlaceCoverageMapping::Identity;
		if (!FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
				Raster.Encoding, Raster.ValueMapping, Kind, OutError))
		{
			return nullptr;
		}

		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = DiskFile;
		Task->DestinationPath = DestPackagePath / TEXT("CoverageRasters");
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

		// Settings are keyed off what the engine ACTUALLY produced, not off the declared encoding
		// alone, and a contradiction between the two is refused here. The texture is discarded on
		// refusal so a wrong-precision asset can never be left behind for a consumer to find.
		FMantlePlaceCoverageTextureSettings Settings;
		if (!FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
				Raster.Encoding, Texture->Source.GetFormat(), Settings, OutError))
		{
			Texture->MarkAsGarbage();
			return nullptr;
		}

		// Applied AFTER the import, never before it. UTextureFactory overwrites its own
		// CompressionSettings from the texture as it starts ("start with the value that the loader
		// suggests"), so a factory configured up front is silently ignored — and the non-power-of-2
		// import rules would otherwise leave these as 8-bit BGRA8 with SRGB on.
		Texture->CompressionSettings = Settings.Compression;
		Texture->SRGB = Settings.bSRGB;
		Texture->MipGenSettings = Settings.MipGen;
		Texture->Filter = Settings.Filter;
		Texture->VirtualTextureStreaming = Settings.bVirtualTextureStreaming;

		// These rasters are north-up windows over one AOI, not tiling patterns; wrapping would
		// repeat the AOI rather than end it, the same reason the drape clamps.
		Texture->AddressX = TA_Clamp;
		Texture->AddressY = TA_Clamp;
		Texture->PostEditChange();

		UMantlePlaceCoverageRasterData* Data = NewObject<UMantlePlaceCoverageRasterData>(Texture);
		Data->Mapping = Kind;
		Data->LayerName = Layer.Name;
		Data->Encoding = Raster.Encoding;
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
		MantlePlaceImportNaming::RenameToConvention(Texture);
		return Texture;
	}
}
