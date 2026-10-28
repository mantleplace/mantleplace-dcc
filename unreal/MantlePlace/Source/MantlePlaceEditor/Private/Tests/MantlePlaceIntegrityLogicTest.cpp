// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceIntegrityLogic.h"

// What the integrity pre-check covers, asserted as a LIST rather than reviewed as a boolean
// expression. The expression is how `Landcover/TreePoints.csv` came to be the one payload the
// importer brought in unverified while the manifest had been publishing a digest for it: a payload
// left out of a hand-written chain is invisible, nothing fails, and no test can see the hole.
//
// The corpus pins the two states on real manifests (manifest.foliagePointsVerified /
// .foliagePointsUnverified, through `integrityVerifiedPaths`). What is asserted here is the
// structural property those fixtures cannot state: that EVERY payload a manifest declares is in the
// list, whichever combination of blocks a bundle happens to ship.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceIntegrityLogicTest,
    "MantlePlace.Import.IntegrityLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
bool Covers(const TArray<FMantlePlaceDeclaredArtifact>& Artifacts, const TCHAR* Path)
{
	return Artifacts.ContainsByPredicate(
	    [Path](const FMantlePlaceDeclaredArtifact& Artifact) { return Artifact.Path == Path; });
}
} // namespace

bool FMantlePlaceIntegrityLogicTest::RunTest(const FString& Parameters)
{
	// --- Every declared payload is in the chain ------------------------------------------------
	{
		FMantlePlaceVaultManifest M;
		M.Heightmap.bPresent = true;
		M.Heightmap.Path = TEXT("Elevation/Heightmap.png");
		M.Heightmap.Sha256 = TEXT("aa");
		M.Drape.bPresent = true;
		M.Drape.Path = TEXT("Imagery/Drape.png");
		M.Drape.Sha256 = TEXT("bb");
		M.Mesh.bPresent = true;
		M.Mesh.Path = TEXT("Mesh/Terrain.glb");
		M.Mesh.Sha256 = TEXT("cc");
		M.Buildings.bPresent = true;
		M.Buildings.Path = TEXT("Mesh/Buildings.glb");
		M.Buildings.Sha256 = TEXT("dd");
		M.RoadSplines.bPresent = true;
		M.RoadSplines.Path = TEXT("Vector/RoadSplines.geojson");
		M.RoadSplines.Sha256 = TEXT("ee");
		M.Foliage.bPresent = true;
		M.Foliage.Path = TEXT("Landcover/TreePoints.csv");
		M.Foliage.Sha256 = TEXT("ff");

		FMantlePlaceLandscapeLayer Layer;
		Layer.Name = TEXT("material_weights");
		Layer.Path = TEXT("Landcover/MaterialWeights.tif");
		Layer.Sha256 = TEXT("11");
		FMantlePlaceUeReadyRaster Companion;
		Companion.Path = TEXT("Landcover/MaterialWeights_1_4.png");
		Companion.Sha256 = TEXT("22");
		Layer.UeReady.Add(Companion);
		M.LandscapeLayers.bPresent = true;
		M.LandscapeLayers.Layers.Add(Layer);

		const TArray<FMantlePlaceDeclaredArtifact> Artifacts =
			FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(M);

		TestEqual(TEXT("every declared payload is collected"), Artifacts.Num(), 8);
		TestTrue(TEXT("heightmap"), Covers(Artifacts, TEXT("Elevation/Heightmap.png")));
		TestTrue(TEXT("drape"), Covers(Artifacts, TEXT("Imagery/Drape.png")));
		TestTrue(TEXT("terrain mesh"), Covers(Artifacts, TEXT("Mesh/Terrain.glb")));
		TestTrue(TEXT("buildings"), Covers(Artifacts, TEXT("Mesh/Buildings.glb")));
		TestTrue(TEXT("road splines"), Covers(Artifacts, TEXT("Vector/RoadSplines.geojson")));
		// The regression this whole unit exists for.
		TestTrue(TEXT("tree points are no longer the exception"),
			Covers(Artifacts, TEXT("Landcover/TreePoints.csv")));
		TestTrue(TEXT("landscape layer source"), Covers(Artifacts, TEXT("Landcover/MaterialWeights.tif")));
		TestTrue(TEXT("its UE-ready companion"),
			Covers(Artifacts, TEXT("Landcover/MaterialWeights_1_4.png")));

		TestEqual(TEXT("all eight are verifiable"),
			FMantlePlaceIntegrityLogic::VerifiablePaths(M).Num(), 8);
	}

	// --- A payload with no published digest is listed, and reported unverified ------------------
	// Dropping it here would make "valid but unverified" indistinguishable from "not shipped", and
	// the third state is the whole point (spec/format.md section 5).
	{
		FMantlePlaceVaultManifest M;
		M.Heightmap.bPresent = true;
		M.Heightmap.Path = TEXT("Elevation/Heightmap.png");
		M.Heightmap.Sha256 = TEXT("aa");
		M.Foliage.bPresent = true;
		M.Foliage.Path = TEXT("Landcover/TreePoints.csv");
		// No FoliagePointsSha256: the schema keeps it optional, and the facts come from a
		// best-effort sidecar.

		const TArray<FMantlePlaceDeclaredArtifact> Artifacts =
			FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(M);
		TestEqual(TEXT("both payloads are listed"), Artifacts.Num(), 2);
		TestTrue(TEXT("the digest-less payload is still listed"),
			Covers(Artifacts, TEXT("Landcover/TreePoints.csv")));

		const TArray<FString> Verifiable = FMantlePlaceIntegrityLogic::VerifiablePaths(M);
		TestEqual(TEXT("only the payload with a digest is verifiable"), Verifiable.Num(), 1);
		TestTrue(TEXT("and it is the heightmap"),
			Verifiable.Num() == 1 && Verifiable[0] == TEXT("Elevation/Heightmap.png"));
	}

	// --- A block the bundle does not ship contributes nothing -----------------------------------
	// bHas* false, or a pointer that is empty, are both "not shipped". A path-less entry in the
	// chain would abort every import of a bundle that simply has no tree points.
	{
		FMantlePlaceVaultManifest M;
		M.Heightmap.bPresent = true;
		M.Heightmap.Path = TEXT("Elevation/Heightmap.png");
		M.Heightmap.Sha256 = TEXT("aa");
		M.Foliage.bPresent = true; // claimed, but with no pointer to read
		M.Foliage.Sha256 = TEXT("ff");

		const TArray<FMantlePlaceDeclaredArtifact> Artifacts =
			FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(M);
		TestEqual(TEXT("a pathless payload is not in the chain"), Artifacts.Num(), 1);
	}

	// --- What the archive's own `view` says it holds -------------------------------------------------
	// A selection that leaves a part out is not corruption; a part the archive claims to hold and does
	// not have is. The decision is the `view` and `files`, never the zip listing alone.
	{
		FMantlePlaceArchiveView View;
		View.bDeclared = true;
		View.Kind = TEXT("selection");
		View.Parts = { TEXT("imagery.drape_png@MAX") };
		View.FileParts.Add(TEXT("Records/Parcels.gpkg"), TEXT("records.parcels_gpkg@MAX"));
		View.FileParts.Add(TEXT("Imagery/Imagery.png"), TEXT("imagery.drape_png@MAX"));

		TestTrue(TEXT("a file whose part the selection left out is left out"),
			FMantlePlaceIntegrityLogic::LeavesOut(View, TEXT("Records/Parcels.gpkg")));
		TestTrue(TEXT("and the path matches regardless of case, as the zip reader does"),
			FMantlePlaceIntegrityLogic::LeavesOut(View, TEXT("RECORDS/PARCELS.GPKG")));
		TestFalse(TEXT("a file whose part the archive claims to hold is not left out (a missing one is corruption)"),
			FMantlePlaceIntegrityLogic::LeavesOut(View, TEXT("Imagery/Imagery.png")));
		TestFalse(TEXT("a file the `files` mapping does not list cannot be told from corruption"),
			FMantlePlaceIntegrityLogic::LeavesOut(View, TEXT("Elevation/Heightmap.png")));

		FMantlePlaceArchiveView Everything = View;
		Everything.Kind = TEXT("everything");
		TestFalse(TEXT("an `everything` archive leaves nothing out, whatever its parts say"),
			FMantlePlaceIntegrityLogic::LeavesOut(Everything, TEXT("Records/Parcels.gpkg")));

		FMantlePlaceArchiveView Unknown = View;
		Unknown.Kind = TEXT("some-future-kind");
		TestTrue(TEXT("an unknown kind holds exactly its parts"),
			FMantlePlaceIntegrityLogic::LeavesOut(Unknown, TEXT("Records/Parcels.gpkg")));

		FMantlePlaceArchiveView NoFiles = View;
		NoFiles.FileParts.Empty();
		TestFalse(TEXT("with no `files` mapping there is no way to tell, so nothing is left out"),
			FMantlePlaceIntegrityLogic::LeavesOut(NoFiles, TEXT("Records/Parcels.gpkg")));

		TestFalse(TEXT("an archive with no `view` leaves nothing out"),
			FMantlePlaceIntegrityLogic::LeavesOut(FMantlePlaceArchiveView(), TEXT("Records/Parcels.gpkg")));
	}

	// --- An artifact is skipped only when every payload it makes from is left out ----------------------
	{
		FMantlePlaceVaultManifest M;
		M.Archive.bDeclared = true;
		M.Archive.Kind = TEXT("selection");
		M.Archive.Parts = { TEXT("mesh.glb@MAX") };
		M.Archive.FileParts.Add(TEXT("Records/Parcels.gpkg"), TEXT("records.parcels_gpkg@MAX"));
		M.Archive.FileParts.Add(TEXT("Records/Flood.gpkg"), TEXT("records.flood_gpkg@MAX"));
		M.Archive.FileParts.Add(TEXT("Site/Terrain-Engine.glb"), TEXT("mesh.glb@MAX"));

		TestTrue(TEXT("every payload left out"),
			FMantlePlaceIntegrityLogic::LeavesOutAll(M, { TEXT("Records/Parcels.gpkg"), TEXT("Records/Flood.gpkg") }));
		TestFalse(TEXT("one payload the archive holds keeps the artifact"),
			FMantlePlaceIntegrityLogic::LeavesOutAll(M, { TEXT("Records/Parcels.gpkg"), TEXT("Site/Terrain-Engine.glb") }));
		TestFalse(TEXT("an artifact that declares no payload is never skipped"),
			FMantlePlaceIntegrityLogic::LeavesOutAll(M, {}));
	}

	// --- Whether the archive holds the Cesium terrain tileset ------------------------------------------
	// The refusal's Cesium hint is a statement about the archive, so it is made only when the archive
	// holds the `terrain.cesium` part (or, with no `view`, when the manifest points at the tileset).
	{
		FMantlePlaceVaultManifest M;
		TestFalse(TEXT("no pointer to a tileset: nothing to stream"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Cesium.bHasTerrain = true;
		M.Cesium.TerrainPath = TEXT("CesiumTerrain/layer.json");
		TestTrue(TEXT("an archive with no `view` holds what the manifest points at"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.bDeclared = true;
		M.Archive.Kind = TEXT("everything");
		M.Archive.Parts = { TEXT("imagery.drape_png@MAX") };
		TestTrue(TEXT("an `everything` archive holds the tileset the manifest points at, whatever its parts say"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.Kind = TEXT("selection");
		TestFalse(TEXT("a selection without the part does not hold it, though the manifest still points at it"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.Parts.Add(TEXT("terrain.cesium@MAX"));
		TestTrue(TEXT("a selection that holds the part holds the tileset"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.Kind = TEXT("some-future-kind");
		TestTrue(TEXT("an unknown kind holds exactly its parts, and this is one"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.Parts = { TEXT("terrain.cesiumx@MAX"), TEXT("elevation.terrain.cesium@MAX") };
		TestFalse(TEXT("a part whose token only resembles the Cesium one is not it"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));

		M.Archive.Parts = { TEXT("terrain.cesium@MAX") };
		M.Cesium.bHasTerrain = false;
		M.Cesium.TerrainPath.Empty();
		TestFalse(TEXT("a held part with no pointer to read is not a tileset the stream could serve"),
			FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(M));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
