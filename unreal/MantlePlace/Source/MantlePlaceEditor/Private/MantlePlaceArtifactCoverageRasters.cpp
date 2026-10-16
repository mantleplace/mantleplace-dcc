// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// `unreal.landscape_layers`: the sub-blocks other than material_weights (water_mask, worldcover,
// hillshade, ndvi, slope, aspect, canopy_height). Each becomes a UTexture2D carrying its decoded
// value_mapping and provenance, so a material or PCG graph can read real metres and degrees rather
// than a 0-65535 ramp. Nothing in this plugin samples them — what they LOOK like is product design,
// and the contract's obligation is that they are reachable with meaning. material_weights is the
// landscape handler's: it changes what the Landscape IS.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceCoverageRasters.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceManifestJson.h"

#include "Engine/Texture2D.h"

namespace
{
/** `ue_ready[].value_mapping` — every shape the five encodings use, read into one struct. Absent
 *  keys simply stay at their defaults: the mapping's shape follows the encoding, so reading by
 *  key rather than branching on the encoding means a new encoding is inert rather than lossy. */
FMantlePlaceRasterValueMapping ParseValueMapping(const TSharedPtr<FJsonObject>& Mapping)
{
	using namespace MantlePlaceManifestJson;
	FMantlePlaceRasterValueMapping Out;
	if (!Mapping.IsValid())
	{
		return Out;
	}
	Out.MinValue = GetNumber(Mapping, TEXT("min"));
	Out.MaxValue = GetNumber(Mapping, TEXT("max"));
	Out.ToValueFormula = GetString(Mapping, TEXT("to_value"));
	Out.Units = GetString(Mapping, TEXT("units"));
	Out.bHasNodata = Mapping->HasField(TEXT("nodata_value"));
	Out.NodataValue = GetNumber(Mapping, TEXT("nodata_value"));
	Out.TrueValue = GetNumber(Mapping, TEXT("true_value"));
	Out.FalseValue = GetNumber(Mapping, TEXT("false_value"));
	Out.bIdentity = GetBool(Mapping, TEXT("identity"), false);
	Out.Bands = GetStrings(Mapping, TEXT("bands"));

	const TArray<TSharedPtr<FJsonValue>>* Classes = nullptr;
	if (Mapping->TryGetArrayField(TEXT("classes"), Classes) && Classes != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Classes)
		{
			if (Value.IsValid())
			{
				Out.Classes.Add(static_cast<int32>(FMath::RoundToDouble(Value->AsNumber())));
			}
		}
	}
	return Out;
}

class FCoverageRastersHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("coverage_rasters")); }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactCoverageRasters", "Coverage rasters"); }

	// The whole block, not just the one entry the importer applies. It is addressed to this host by
	// name, so leaving parts unparsed is how "published and ignored" happens a second time.
	// Keys are read off the object and sorted: the block is `additionalProperties: true`, so a
	// future ninth layer arrives without a code change, and JSON object order is not a contract.
	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		if (const TSharedPtr<FJsonObject>* LandscapeLayersPtr = GetObject(In.Unreal, TEXT("landscape_layers")))
		{
			TArray<FString> LayerNames;
			for (const TPair<FString, TSharedPtr<FJsonValue>> Pair : (*LandscapeLayersPtr)->Values)
			{
				LayerNames.Add(Pair.Key);
			}
			LayerNames.Sort();

			for (const FString& LayerName : LayerNames)
			{
				const TSharedPtr<FJsonObject>* EntryPtr = GetObject(*LandscapeLayersPtr, *LayerName);
				if (EntryPtr == nullptr)
				{
					continue; // not an object; the schema says it must be, so there is nothing to read
				}
				const TSharedPtr<FJsonObject> Entry = *EntryPtr;

				FMantlePlaceLandscapeLayer Layer;
				Layer.Name = LayerName;
				Layer.Path = GetString(Entry, TEXT("path"));
				Layer.Sha256 = GetString(Entry, TEXT("sha256"));
				Layer.Materials = GetStrings(Entry, TEXT("materials"));

				const TArray<TSharedPtr<FJsonValue>>* UeReady = nullptr;
				if (Entry->TryGetArrayField(TEXT("ue_ready"), UeReady) && UeReady != nullptr)
				{
					for (const TSharedPtr<FJsonValue>& Value : *UeReady)
					{
						const TSharedPtr<FJsonObject> RasterObject = Value.IsValid() ? Value->AsObject() : nullptr;
						if (!RasterObject.IsValid())
						{
							continue;
						}
						FMantlePlaceUeReadyRaster Raster;
						Raster.Path = GetString(RasterObject, TEXT("path"));
						Raster.Sha256 = GetString(RasterObject, TEXT("sha256"));
						Raster.Encoding = GetString(RasterObject, TEXT("encoding"));
						Raster.Width = GetInt(RasterObject, TEXT("width"));
						Raster.Height = GetInt(RasterObject, TEXT("height"));
						Raster.SizeBytes = static_cast<int64>(GetNumber(RasterObject, TEXT("size_bytes")));
						if (const TSharedPtr<FJsonObject>* MappingPtr = GetObject(RasterObject, TEXT("value_mapping")))
						{
							Raster.ValueMapping = ParseValueMapping(*MappingPtr);
						}
						Layer.UeReady.Add(MoveTemp(Raster));
					}
				}
				M.LandscapeLayers.Layers.Add(MoveTemp(Layer));
			}
		}
		M.LandscapeLayers.bPresent = M.LandscapeLayers.Layers.Num() > 0;
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.LandscapeLayers.bPresent; }

	// Every landscape layer and every UE-ready companion, including the layers this importer parses
	// but does not yet apply: the check is on the bundle's bytes, not on what today's importer
	// happens to read.
	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		const auto Add = [&Out](const FString& Path, const FString& Sha256)
		{
			if (!Path.IsEmpty())
			{
				Out.Add(FMantlePlaceDeclaredArtifact{ Path, Sha256 });
			}
		};
		for (const FMantlePlaceLandscapeLayer& Layer : M.LandscapeLayers.Layers)
		{
			Add(Layer.Path, Layer.Sha256);
			for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
			{
				Add(Raster.Path, Raster.Sha256);
			}
		}
	}

	// Independent of Mode: they describe the ground rather than being a terrain representation, so
	// like the buildings and road splines they import whenever the bundle ships them. Per-raster
	// warn-and-skip, deliberately unlike the fail-closed weights path — a missing analysis channel
	// costs a texture, not a wrong Landscape. (Their bytes are still fail-closed: a sha256 mismatch
	// aborted in the integrity pre-check, before any actor.)
	void Import(FMantlePlaceImportContext& Ctx) override
	{
		int32 RastersImported = 0;
		TArray<FString> SkippedLayers;
		for (const FMantlePlaceLandscapeLayer& Layer : Ctx.Manifest.LandscapeLayers.Layers)
		{
			if (!FMantlePlaceCoverageRasterLogic::IsCoverageRaster(Layer.Name))
			{
				continue;
			}
			for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
			{
				FString Err, RasterDisk;
				UTexture2D* Texture = nullptr;
				if (Ctx.Extract(Raster.Path, RasterDisk, Err))
				{
					Texture = MantlePlaceCoverageRasters::ImportRaster(
						Layer, Raster, RasterDisk, Ctx.Manifest.JobId, Ctx.DestPackagePath, Err);
				}
				if (Texture != nullptr)
				{
					++RastersImported;
				}
				else
				{
					SkippedLayers.Add(Layer.Name);
					Ctx.Log.Add(FString::Printf(TEXT("Coverage raster \"%s\" skipped: %s"), *Layer.Name, *Err));
				}
			}
		}
		if (RastersImported > 0)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Imported %d coverage raster(s) as data textures with their value mappings attached."),
				RastersImported));
		}
		if (SkippedLayers.Num() > 0)
		{
			// Name the skipped set as a set, not only one line each: a reader scanning the log for
			// what they got needs the gap to be as visible as the success count.
			Ctx.Log.Add(FString::Printf(TEXT("Coverage rasters not imported: %s."), *FString::Join(SkippedLayers, TEXT(", "))));
		}
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeCoverageRasters() { return MakeUnique<FCoverageRastersHandler>(); }
}
