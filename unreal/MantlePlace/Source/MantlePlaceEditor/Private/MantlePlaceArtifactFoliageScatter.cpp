// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

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

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override { return true; }
	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Foliage.bPresent; }
	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override {}

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

		if (Ctx.Manifest.HasLevelOffset())
		{
			Ctx.Log.Add(TEXT("Foliage scatter skipped: this bundle is placed beside another import in the level, "
				"and the scatter samples its coverage rasters about the world origin. The tree-point table is "
				"imported and placed at the offset."));
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
