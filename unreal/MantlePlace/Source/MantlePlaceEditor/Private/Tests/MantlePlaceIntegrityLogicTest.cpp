// Copyright Mantle Place. All Rights Reserved.

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
		M.bHasHeightmap = true;
		M.HeightmapPath = TEXT("Elevation/Heightmap.png");
		M.HeightmapSha256 = TEXT("aa");
		M.bHasDrape = true;
		M.DrapePath = TEXT("Imagery/Drape.png");
		M.DrapeSha256 = TEXT("bb");
		M.bHasMesh = true;
		M.MeshPath = TEXT("Mesh/Terrain.glb");
		M.MeshSha256 = TEXT("cc");
		M.bHasBuildings = true;
		M.BuildingsPath = TEXT("Mesh/Buildings.glb");
		M.BuildingsSha256 = TEXT("dd");
		M.bHasRoadSplines = true;
		M.RoadSplinesPath = TEXT("Vector/RoadSplines.geojson");
		M.RoadSplinesSha256 = TEXT("ee");
		M.bHasFoliagePoints = true;
		M.FoliagePointsPath = TEXT("Landcover/TreePoints.csv");
		M.FoliagePointsSha256 = TEXT("ff");

		FMantlePlaceLandscapeLayer Layer;
		Layer.Name = TEXT("material_weights");
		Layer.Path = TEXT("Landcover/MaterialWeights.tif");
		Layer.Sha256 = TEXT("11");
		FMantlePlaceUeReadyRaster Companion;
		Companion.Path = TEXT("Landcover/MaterialWeights_1_4.png");
		Companion.Sha256 = TEXT("22");
		Layer.UeReady.Add(Companion);
		M.bHasLandscapeLayers = true;
		M.LandscapeLayers.Add(Layer);

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
		M.bHasHeightmap = true;
		M.HeightmapPath = TEXT("Elevation/Heightmap.png");
		M.HeightmapSha256 = TEXT("aa");
		M.bHasFoliagePoints = true;
		M.FoliagePointsPath = TEXT("Landcover/TreePoints.csv");
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
		M.bHasHeightmap = true;
		M.HeightmapPath = TEXT("Elevation/Heightmap.png");
		M.HeightmapSha256 = TEXT("aa");
		M.bHasFoliagePoints = true; // claimed, but with no pointer to read
		M.FoliagePointsSha256 = TEXT("ff");

		const TArray<FMantlePlaceDeclaredArtifact> Artifacts =
			FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(M);
		TestEqual(TEXT("a pathless payload is not in the chain"), Artifacts.Num(), 1);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
