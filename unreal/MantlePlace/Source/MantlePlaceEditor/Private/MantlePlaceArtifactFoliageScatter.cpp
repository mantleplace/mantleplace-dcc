// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// The PCG foliage scatter: generated from what two other handlers left, the tree-points table and
// the coverage rasters, rather than read from a block of its own. Opt-in (FromMode leaves it out
// unless asked), so an import that did not ask for foliage spawns none.
//
// It builds the order's foliage graph beside the table, spawns a volume over the AOI, claims it
// (so in a World Partition level it joins the foliage Data Layer, and the PCG partition actors made
// for it inherit that layer), and starts generation: partitioned on the PCG grid in a World
// Partition level, so foliage generates and streams per cell, and on the volume itself otherwise.
// A re-import's wipe sweeps what it generated before destroying the volume.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceFoliageScatter.h"
#include "MantlePlaceFoliageVolume.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"

#include "Engine/World.h"

namespace
{
class FFoliageScatterHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return MantlePlaceArtifactId::FoliageScatter(); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Foliage; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactFoliageScatter", "Foliage scatter"); }

	// No block of its own: it reads what the tree points and the coverage rasters declared.
	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override { return true; }
	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Foliage.bPresent; }
	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override {}

	// The scatter is generated, not read from a file of its own: it is made from the tree points and the
	// coverage rasters it filters by, so those files' roles are its roles.
	void CollectSourcePaths(const FMantlePlaceVaultManifest& M, TArray<FString>& Out) const override
	{
		if (!M.Foliage.Path.IsEmpty())
		{
			Out.Add(M.Foliage.Path);
		}
		for (const TCHAR* Key : { TEXT("canopy_height"), TEXT("water_mask"), TEXT("worldcover") })
		{
			if (const FMantlePlaceLandscapeLayer* Layer = M.FindLandscapeLayer(Key))
			{
				// The UE-ready PNGs are what the importer reads; the source GeoTIFF is only the fallback.
			for (const FMantlePlaceUeReadyRaster& Raster : Layer->UeReady)
			{
				Out.Add(Raster.Path);
			}
			if (Layer->UeReady.IsEmpty())
			{
				Out.Add(Layer->Path);
			}
			}
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		if (Ctx.TreePointsTable == nullptr)
		{
			if (Ctx.Manifest.Foliage.bPresent)
			{
				Ctx.Log.Add(TEXT("Foliage scatter skipped: no tree-points table was made to scatter from."));
			}
			return;
		}

		MantlePlaceFoliageScatter::FInputs Inputs;
		Inputs.TreePoints = Ctx.TreePointsTable;
		Inputs.CanopyHeight = Ctx.CoverageTextures.FindRef(TEXT("canopy_height"));
		Inputs.WaterMask = Ctx.CoverageTextures.FindRef(TEXT("water_mask"));
		Inputs.LandCover = Ctx.CoverageTextures.FindRef(TEXT("worldcover"));
		Inputs.AoiSizeCm = Ctx.Manifest.GetAoiSizeUeCm();
		Inputs.GroundMinZCm = Ctx.Manifest.Heightmap.ZMinM * 100.0;
		Inputs.GroundMaxZCm = Ctx.Manifest.Heightmap.ZMaxM * 100.0;
		const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
		Inputs.TreeMeshes = Settings->FoliageTreeMeshes;
		Inputs.ShrubMeshes = Settings->FoliageShrubMeshes;

		FString Error;
		UPCGGraph* Graph = MantlePlaceFoliageScatter::BuildGraph(
			MantlePlaceImportNaming::SubfolderPath(Ctx.DestPackagePath, MantlePlaceImportNaming::ESubfolder::Landcover),
			MantlePlaceImportNaming::FoliageGraphName(), Inputs, Error);
		AMantlePlaceFoliageVolume* Volume = Graph != nullptr ? MantlePlaceFoliageScatter::SpawnVolume(Ctx.World, Inputs) : nullptr;
		if (Volume == nullptr)
		{
			Ctx.Log.Add(FString::Printf(TEXT("Foliage scatter skipped: %s."),
				Error.IsEmpty() ? TEXT("the volume could not be spawned") : *Error));
			return;
		}
		// Claimed BEFORE the graph is assigned: the partition actors take the volume's Data Layer when made.
		Ctx.Claim(Volume, MantlePlaceImportNaming::ActorLabel(MantlePlaceImportNaming::EActorKind::Foliage, Ctx.Identity));
		MantlePlaceFoliageScatter::Generate(Volume, Graph);

		TArray<FString> Missing;
		if (Inputs.CanopyHeight == nullptr) { Missing.Add(TEXT("canopy_height")); }
		if (Inputs.WaterMask == nullptr) { Missing.Add(TEXT("water_mask")); }
		if (Inputs.LandCover == nullptr) { Missing.Add(TEXT("worldcover")); }
		Ctx.Log.Add(FString::Printf(TEXT("Foliage scatter generating (%s, graph %s)%s."),
			UWorld::IsPartitionedWorld(Ctx.World) ? TEXT("partitioned per World Partition cell") : TEXT("on one volume: the level is not partitioned"),
			*MantlePlaceImportNaming::FoliageGraphName(),
			Missing.IsEmpty() ? TEXT("") : *FString::Printf(TEXT("; not filtered by %s, which the bundle does not ship"), *FString::Join(Missing, TEXT(", ")))));
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliageScatter() { return MakeUnique<FFoliageScatterHandler>(); }
}
