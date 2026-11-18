// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLiveGateLogic.h"
#include "MantlePlaceManifestThreeLogic.h"

// The drape smoke check of the live import test (`MantlePlace.Import.LiveFreeTier`), asserted headless on a
// 3.0 bundle whose Imagery/Imagery.png states no rectangle. The importer skips that drape by design
// (HPS-53), so the landscape stays on the engine default surface and the check must assert the skip is
// reported, not a draped material. The live test needs an editor and a whole bundle; these do not.

namespace
{
/** A 3.0 manifest of one order. `ImageryRaster` is the raster object of Imagery/Imagery.png. */
FString ManifestWithImagery(const FString& ImageryRaster)
{
	return FString::Printf(TEXT(R"JSON({
  "version": "3.0.0",
  "order": {"id": "order-695", "aoi": {"west": -1, "south": 2, "east": 3, "north": 4}, "unit_system": "metric"},
  "level": "Med",
  "frames": {
    "georeferenced": {"horizontal_crs": {"code": "EPSG:32614"}, "vertical_crs": {"code": "EPSG:3855"}, "geoid_model": null,
                      "horizontal_unit": {"name": "metre", "to_metres": 1},
                      "vertical_unit": {"name": "metre", "to_metres": 1},
                      "axis_order": "east-north", "grid_rotation_deg": 0.0},
    "local": {"origin": {"longitude": -97.7, "latitude": 30.3, "ellipsoidal_height_m": 70.0, "height_m": 100.0,
                         "projected": {"crs": {"code": "EPSG:32614"}, "x": 500000.0, "y": 4000000.0, "unit": {"name": "metre", "to_metres": 1}}},
              "vertical_crs": {"code": "EPSG:3855"}, "geoid_model": null, "unit": {"name": "metre", "to_metres": 1},
              "axes": {"x": "east", "y": "north", "z": "up"}, "grid_rotation_deg": 0.0}
  },
  "catalog": {
    "Elevation/Elevation.png": {"family": "host-ready", "sha256": "aa", "count": {"kind": "pixels", "value": 509545}, "raster": {"width": 1009, "height": 505,
        "bands": ["height"], "data_type": "UInt16", "scale": 0.01, "offset": -12.5, "unit": "metre", "nodata": null, "frame": "local",
        "rectangle": [-1008, -500, 1008, 516], "cell_size": [2, 2]}},
    "Imagery/Imagery.png": {"family": "host-ready", "sha256": "bb", "raster": {%s}},
    "Elevation/Terrain.glb": {"family": "host-ready", "sha256": "cc", "mesh": {"up_axis": "y"}}
  },
  "not_held": []
})JSON"), *ImageryRaster);
}

bool ConvertsTo(const FString& Text, FMantlePlaceVaultManifest& OutManifest)
{
	const FMantlePlaceBundleRead Read = FMantlePlaceBundleReaderLogic::Read(
		{ TEXT("Manifest.json"), TEXT("Elevation/Elevation.png"), TEXT("Imagery/Imagery.png"), TEXT("Elevation/Terrain.glb") },
		[&Text](const FString& Name, FString& OutText) { OutText = Text; return Name.EndsWith(TEXT("Manifest.json")); });
	FString Error;
	return Read.IsRead() && FMantlePlaceManifestThreeLogic::ToVaultManifest(Read, Text, OutManifest, Error);
}

const TCHAR* const NoRectangle =
	TEXT("\"width\": 1024, \"height\": 1024, \"bands\": [\"red\", \"green\", \"blue\"], \"data_type\": \"Byte\", "
	     "\"scale\": 1, \"offset\": 0, \"frame\": \"local\"");
const TCHAR* const WithRectangle =
	TEXT("\"width\": 1024, \"height\": 1024, \"bands\": [\"red\", \"green\", \"blue\"], \"data_type\": \"Byte\", "
	     "\"scale\": 1, \"offset\": 0, \"frame\": \"local\", \"rectangle\": [-1000, -1000, 1000, 1000]");
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLiveDrapeCheckTest,
	"MantlePlace.Import.LiveDrapeCheck",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLiveDrapeCheckTest::RunTest(const FString& Parameters)
{
	// The import's own end-of-run line for a landscape that got no drape.
	const FString GeometryOnly = TEXT("No imagery drape in this bundle (geometry only).");

	// --- A 3.0 bundle whose imagery states no rectangle: the drape is skipped, the skip is named --------
	{
		FMantlePlaceVaultManifest M;
		if (!TestTrue(TEXT("the manifest converts"), ConvertsTo(ManifestWithImagery(NoRectangle), M)))
		{
			return false;
		}
		TestFalse(TEXT("no drape is placed"), M.Drape.bPresent);
		const FString SkipLine = FMantlePlaceLiveGateLogic::DrapeSkipLine(M);
		TestTrue(TEXT("the skip is the importer's own line, naming the file and the missing rectangle: ") + SkipLine,
			SkipLine.StartsWith(TEXT("Skipped ")) && SkipLine.Contains(TEXT("Imagery/Imagery.png"))
				&& SkipLine.Contains(TEXT("states no rectangle")));

		const FString Reported = SkipLine + TEXT("\n") + GeometryOnly;
		TestEqual(TEXT("the landscape on the default surface passes when the skip is reported"),
			FMantlePlaceLiveGateLogic::JudgeDrape(M, Reported, /*bMaterialIsDrape*/ false), FString());
		TestEqual(TEXT("it still passes if the landscape did get a material"),
			FMantlePlaceLiveGateLogic::JudgeDrape(M, Reported, /*bMaterialIsDrape*/ true), FString());
		TestTrue(TEXT("it fails when the import never said it skipped the drape"),
			!FMantlePlaceLiveGateLogic::JudgeDrape(M, GeometryOnly, /*bMaterialIsDrape*/ false).IsEmpty());
		TestTrue(TEXT("it fails on an empty import log"),
			!FMantlePlaceLiveGateLogic::JudgeDrape(M, FString(), /*bMaterialIsDrape*/ false).IsEmpty());
	}

	// --- The same bundle with a stated rectangle: the drape is placed, so the material must carry it -----
	{
		FMantlePlaceVaultManifest M;
		if (!TestTrue(TEXT("the manifest converts"), ConvertsTo(ManifestWithImagery(WithRectangle), M)))
		{
			return false;
		}
		TestTrue(TEXT("the drape is placed"), M.Drape.bPresent);
		TestTrue(TEXT("no skip is named for it"), FMantlePlaceLiveGateLogic::DrapeSkipLine(M).IsEmpty());
		TestEqual(TEXT("a drape that reached the landscape material passes"),
			FMantlePlaceLiveGateLogic::JudgeDrape(M, TEXT("Imagery draped onto its geographic footprint."), true), FString());
		TestTrue(TEXT("a drape placed but left off the landscape material fails"),
			!FMantlePlaceLiveGateLogic::JudgeDrape(M, TEXT("Imagery draped onto its geographic footprint."), false).IsEmpty());
		// A skip line in the log does not excuse a drape the manifest placed.
		TestTrue(TEXT("a reported skip does not excuse a placed drape"),
			!FMantlePlaceLiveGateLogic::JudgeDrape(M, GeometryOnly, false).IsEmpty());
	}

	// --- A manifest that declares no drape and names no skip (a 2.x bundle without imagery) -------------
	{
		FMantlePlaceVaultManifest M;
		TestFalse(TEXT("no drape is placed"), M.Drape.bPresent);
		TestTrue(TEXT("no skip is named"), FMantlePlaceLiveGateLogic::DrapeSkipLine(M).IsEmpty());
		TestEqual(TEXT("the geometry-only line is enough when nothing was declared"),
			FMantlePlaceLiveGateLogic::JudgeDrape(M, GeometryOnly, false), FString());
		TestTrue(TEXT("and its absence fails"),
			!FMantlePlaceLiveGateLogic::JudgeDrape(M, TEXT("Imported."), false).IsEmpty());
	}

	// --- A 3.0 bundle that lacks the imagery entry: the importer names that skip too ----------------------
	{
		FMantlePlaceVaultManifest M;
		if (!TestTrue(TEXT("the manifest converts"),
				ConvertsTo(ManifestWithImagery(WithRectangle).Replace(TEXT("Imagery/Imagery.png"), TEXT("Imagery/Other.png")), M)))
		{
			return false;
		}
		TestFalse(TEXT("no drape is placed"), M.Drape.bPresent);
		TestFalse(TEXT("a skip is named for it"), FMantlePlaceLiveGateLogic::DrapeSkipLine(M).IsEmpty());
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
