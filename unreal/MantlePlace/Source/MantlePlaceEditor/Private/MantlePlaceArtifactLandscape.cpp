// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// The heightmap: the headline path, a native Landscape built from the pre-baked heightmap PNG, with
// the bundle's material-weight layers painted on it when it ships them.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLandscapeImporter.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceManifestJson.h"

#include "Landscape.h"

namespace
{
class FLandscapeHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return MantlePlaceArtifactId::Landscape(); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Terrain; }
	FName EquivalenceGroup() const override { return FName(TEXT("terrain")); } // the Landscape and the Mesh stand for the same ground
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactLandscape", "Landscape"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		const TSharedPtr<FJsonObject>* HeightmapPtr = GetObject(In.Unreal, TEXT("heightmap"));
		if (HeightmapPtr == nullptr)
		{
			return true;
		}
		const TSharedPtr<FJsonObject> H = *HeightmapPtr;
		FMantlePlaceHeightmapBlock& Heightmap = M.Heightmap;
		Heightmap.Path = GetString(H, TEXT("path"));
		Heightmap.Resolution = GetInt(H, TEXT("resolution"));
		Heightmap.bRow0IsNorth = GetBool(H, TEXT("row0_is_north"), true);
		Heightmap.Sha256 = GetString(H, TEXT("sha256")); // verified against the extracted bytes at import

		// What the 16-bit pixels mean, in metres. A 1.x bundle states it in this host's block
		// (`uint16_mapping`), applied verbatim as before. A 2.0 bundle states it only in the neutral
		// `file_entries[<heightmap path>].value_mapping` (`min` and `max`, the heights codes 0 and 65535
		// stand for), the host-block copy being refused by its schema; so it is read from there when
		// the block does not carry it. Neither path guesses: absent in both, the range stays 0.
		if (const TSharedPtr<FJsonObject>* MapPtr = GetObject(H, TEXT("uint16_mapping")))
		{
			Heightmap.ZMinM = GetNumber(*MapPtr, TEXT("z_min_m"));
			Heightmap.ZMaxM = GetNumber(*MapPtr, TEXT("z_max_m"));
		}
		else if (const TSharedPtr<FJsonObject> Entry = FindFileEntry(In.Root, Heightmap.Path); Entry.IsValid())
		{
			if (const TSharedPtr<FJsonObject>* NeutralPtr = GetObject(Entry, TEXT("value_mapping")))
			{
				Heightmap.ZMinM = GetNumber(*NeutralPtr, TEXT("min"));
				Heightmap.ZMaxM = GetNumber(*NeutralPtr, TEXT("max"));
			}
		}

		if (const TSharedPtr<FJsonObject>* XformPtr = GetObject(H, TEXT("landscape_transform")))
		{
			const TSharedPtr<FJsonObject> X = *XformPtr;
			Heightmap.ScaleXPercent = GetNumber(X, TEXT("scale_x_percent"));
			Heightmap.ScaleYPercent = GetNumber(X, TEXT("scale_y_percent"));
			Heightmap.ScaleZPercent = GetNumber(X, TEXT("z_scale_percent"));
			Heightmap.LocationZOffsetCm = GetNumber(X, TEXT("location_z_offset_cm"));
			Heightmap.SectionSizeQuads = GetInt(X, TEXT("section_size_quads"));
			Heightmap.SectionsPerComponent = GetInt(X, TEXT("sections_per_component"));
			Heightmap.ComponentCountX = GetInt(X, TEXT("component_count_x"));
			Heightmap.ComponentCountY = GetInt(X, TEXT("component_count_y"));
		}

		Heightmap.bPresent = !Heightmap.Path.IsEmpty() && Heightmap.Resolution > 0
			&& Heightmap.SectionSizeQuads > 0 && Heightmap.SectionsPerComponent > 0
			&& Heightmap.ComponentCountX > 0 && Heightmap.ComponentCountY > 0;

		// The schema requires heightmap.sha256, so an absent hash is a malformed manifest, not a
		// legacy bundle — fail closed rather than silently skip the check.
		if (Heightmap.bPresent && Heightmap.Sha256.IsEmpty())
		{
			OutError = TEXT("manifest hosts.unreal.heightmap has no sha256 (schema-required); refusing to import unverifiable bytes.");
			return false;
		}
		return true;
	}

	bool CrossCheck(const FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		// Consistency: the recommended-size formula must reproduce the stated resolution.
		if (!M.Heightmap.bPresent)
		{
			return true;
		}
		const int32 Derived = M.Heightmap.ComponentCountX * M.GetQuadsPerComponent() + 1;
		if (Derived != M.Heightmap.Resolution)
		{
			OutError = FString::Printf(
				TEXT("Heightmap resolution %d != component_count_x(%d)*quads_per_component(%d)+1 = %d."),
				M.Heightmap.Resolution, M.Heightmap.ComponentCountX, M.GetQuadsPerComponent(), Derived);
			return false;
		}
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Heightmap.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Heightmap.bPresent && !M.Heightmap.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.Heightmap.Path, M.Heightmap.Sha256 });
		}
	}

	bool IsTerrainRepresentation() const override { return true; }

	void ExplainAbsence(const FMantlePlaceVaultManifest& M, TArray<FString>& OutReasons) const override
	{
		if (!M.Heightmap.bPresent)
		{
			OutReasons.Add(TEXT("this bundle has no heightmap"));
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.Heightmap.bPresent)
		{
			Ctx.Log.Add(TEXT("Landscape requested but the bundle has no heightmap."));
			Ctx.bAllRequestedSucceeded = false;
			return;
		}

		// Material-weight layers, if the bundle published them. Non-fatal: a landscape with the
		// drape and no weight layers is what every bundle produced before this, so a decode
		// failure degrades to that rather than failing the import.
		TArray<FMantlePlaceWeightPlane> WeightPlanes;
		FIntPoint WeightRasterSize(0, 0); // what the planes were resampled FROM, for the log
		if (const FMantlePlaceLandscapeLayer* Weights = Manifest.FindLandscapeLayer(TEXT("material_weights")))
		{
			FString WeightsErr;
			TArray<FMantlePlaceRgbaImage> Images;
			bool bDecoded = true;
			for (const FMantlePlaceUeReadyRaster& Raster : Weights->UeReady)
			{
				WeightRasterSize = FIntPoint(Raster.Width, Raster.Height);
				FString RasterDisk;
				FMantlePlaceRgbaImage Image;
				if (!Ctx.Extract(Raster.Path, RasterDisk, WeightsErr)
					|| !MantlePlaceLandscapeImporter::DecodeRgbaPng(RasterDisk, Raster.Path, Image, WeightsErr))
				{
					bDecoded = false;
					break;
				}
				Images.Add(MoveTemp(Image));
			}
			if (bDecoded)
			{
				// ⛔ Hotspot: nearest-neighbour resampling of every weight plane. Measured on
				// its own rather than inside the landscape phase, because the point of the
				// baseline is to size this against the shader stall it shares a phase with.
				const MantlePlaceImportTiming::FScopedPhase ResamplePhase(
					Ctx.Timeline, MantlePlaceImportTiming::Phase::WeightResample,
					FString::Printf(TEXT("%d plane(s)"), Images.Num()));
				bDecoded = FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
					*Weights, Images, Manifest.Heightmap.Resolution, Manifest.Heightmap.bRow0IsNorth, WeightPlanes, WeightsErr);
			}
			if (!bDecoded)
			{
				WeightPlanes.Reset();
				Ctx.Log.Add(FString::Printf(TEXT("Landscape material layers skipped: %s"), *WeightsErr));
			}
		}

		const int32 Resolution = Manifest.Heightmap.Resolution;
		const MantlePlaceImportTiming::FScopedPhase LandscapePhase(
			Ctx.Timeline, MantlePlaceImportTiming::Phase::Artifact,
			FString::Printf(TEXT("landscape %dx%d"), Resolution, Resolution));
		FString Err, HeightmapDisk;

		// Inside the landscape phase and measured on its own: laying the heightmap on disk is
		// an inflate plus a write of the same bytes the decode below then reads back, and it is
		// the one step of the landscape artifact that happens before control leaves this file.
		bool bHeightmapOnDisk = false;
		{
			const MantlePlaceImportTiming::FScopedPhase HeightmapExtractPhase(
				Ctx.Timeline, MantlePlaceImportTiming::Phase::LandscapeHeightmapExtract);
			bHeightmapOnDisk = Ctx.Extract(Manifest.Heightmap.Path, HeightmapDisk, Err);
		}

		if (!bHeightmapOnDisk)
		{
			Ctx.Log.Add(Err);
			Ctx.bAllRequestedSucceeded = false;
			return;
		}
		if (ALandscape* Landscape = MantlePlaceLandscapeImporter::Import(
				Ctx.World, Manifest, HeightmapDisk, Ctx.DrapeMic, WeightPlanes, Ctx.DestPackagePath, Err))
		{
			Ctx.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(
				MantlePlaceImportNaming::EActorKind::Landscape, Ctx.Identity));
			Ctx.DrapeTargets.Add(Landscape);
			Ctx.Result.CreatedActors.Add(Landscape->GetActorLabel());
			Ctx.Log.Add(FString::Printf(TEXT("Landscape created (%dx%d)."), Resolution, Resolution));
			if (WeightPlanes.Num() > 0)
			{
				// The weight rasters are on the DEM's grid, not the Landscape's, so say what
				// was resampled onto what rather than letting the numbers look identical.
				Ctx.Log.Add(FString::Printf(
					TEXT("Painted %d material weight layer(s), resampled from %dx%d onto the %dx%d grid."),
					WeightPlanes.Num(), WeightRasterSize.X, WeightRasterSize.Y, Resolution, Resolution));
			}
		}
		else
		{
			Ctx.Log.Add(FString::Printf(TEXT("Landscape failed: %s"), *Err));
			Ctx.bAllRequestedSucceeded = false;
		}
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeLandscape() { return MakeUnique<FLandscapeHandler>(); }
}
