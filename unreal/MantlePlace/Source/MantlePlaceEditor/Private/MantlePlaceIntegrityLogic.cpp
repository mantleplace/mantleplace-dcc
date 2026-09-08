// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceIntegrityLogic.h"

#include "MantlePlaceImportManifest.h"

TArray<FMantlePlaceDeclaredArtifact> FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(
    const FMantlePlaceVaultManifest& Manifest)
{
	TArray<FMantlePlaceDeclaredArtifact> Artifacts;

	const auto Add = [&Artifacts](bool bPresent, const FString& Path, const FString& Sha256)
	{
		if (!bPresent || Path.IsEmpty())
		{
			return;
		}
		Artifacts.Add(FMantlePlaceDeclaredArtifact{ Path, Sha256 });
	};

	Add(Manifest.bHasHeightmap, Manifest.HeightmapPath, Manifest.HeightmapSha256);
	Add(Manifest.bHasDrape, Manifest.DrapePath, Manifest.DrapeSha256);
	Add(Manifest.bHasMesh, Manifest.MeshPath, Manifest.MeshSha256);
	Add(Manifest.bHasBuildings, Manifest.BuildingsPath, Manifest.BuildingsSha256);
	Add(Manifest.bHasRoadSplines, Manifest.RoadSplinesPath, Manifest.RoadSplinesSha256);

	// The tree points, no longer the exception. The digest comes off `unreal.foliage_points`, this
	// host's own block -- never `landcover.tree_points`, which carries the same value addressed to
	// somebody else (HPS-33). It stays OPTIONAL in the schema, so a bundle without one is imported
	// and reported unverified rather than refused.
	Add(Manifest.bHasFoliagePoints, Manifest.FoliagePointsPath, Manifest.FoliagePointsSha256);

	// Every landscape layer and every UE-ready companion, including the seven layers this importer
	// parses but does not yet apply: the check is on the bundle's bytes, not on what today's
	// importer happens to read.
	for (const FMantlePlaceLandscapeLayer& Layer : Manifest.LandscapeLayers)
	{
		Add(true, Layer.Path, Layer.Sha256);
		for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
		{
			Add(true, Raster.Path, Raster.Sha256);
		}
	}

	return Artifacts;
}

TArray<FString> FMantlePlaceIntegrityLogic::VerifiablePaths(const FMantlePlaceVaultManifest& Manifest)
{
	TArray<FString> Paths;
	for (const FMantlePlaceDeclaredArtifact& Artifact : CollectDeclaredArtifacts(Manifest))
	{
		if (Artifact.IsVerifiable())
		{
			Paths.Add(Artifact.Path);
		}
	}
	return Paths;
}
