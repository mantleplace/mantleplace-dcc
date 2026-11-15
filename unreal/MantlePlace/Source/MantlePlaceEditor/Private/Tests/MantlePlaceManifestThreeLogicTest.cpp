// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceManifestThreeLogic.h"
#include "MantlePlaceTreePointsLogic.h"

// An MPB 3.x bundle read into the shape every artifact handler imports from: which promised file feeds
// each step, its stated values multiplied into this host's convention, and every step a 3.0 bundle
// cannot feed skipped with its reason. `MantlePlace.Import.ManifestThreeLogic`.

namespace
{
/** A 3.0 manifest of one order: the site origin at (500000, 4000000) m and 100 m high, in metres. */
FString Manifest(const FString& LocalUnit = TEXT("{\"name\": \"metre\", \"to_metres\": 1}"),
	const FString& ElevationRaster = TEXT("\"width\": 1009, \"height\": 505, \"bands\": [\"height\"], \"data_type\": \"UInt16\","
		" \"scale\": 0.01, \"offset\": -12.5, \"unit\": \"metre\", \"nodata\": null, \"frame\": \"local\","
		" \"rectangle\": [-1008, -500, 1008, 516], \"cell_size\": [2, 2]"),
	const FString& NotHeld = TEXT("[{\"path\": \"Landcover/Trees.csv\", \"reason\": \"The AOI has no tree cover.\"}]"))
{
	return FString::Printf(TEXT(R"JSON({
  "version": "3.0.0",
  "order": {"id": "order-609", "aoi": {"west": -1, "south": 2, "east": 3, "north": 4}, "unit_system": "imperial"},
  "level": "Med",
  "frames": {
    "georeferenced": {"horizontal_crs": {"code": "EPSG:2277"}, "horizontal_unit": {"name": "US survey foot", "to_metres": 0.3048006096}},
    "local": {"origin": {"longitude": -97.7, "latitude": 30.3, "height_m": 100.0,
                         "projected": {"crs": {"code": "EPSG:32614"}, "x": 500000.0, "y": 4000000.0, "unit": {"name": "metre", "to_metres": 1}}},
              "vertical_crs": {"code": "EPSG:3855"}, "unit": %s, "axes": {"x": "east", "y": "north", "z": "up"}}
  },
  "catalog": {
    "Elevation/Elevation.png": {"family": "host-ready", "sha256": "aa", "count": {"kind": "pixels", "value": 509545}, "raster": {%s}},
    "Imagery/Imagery.png": {"family": "host-ready", "sha256": "bb", "raster": {"width": 1024, "height": 1024, "bands": ["red", "green", "blue"],
        "data_type": "Byte", "scale": 1, "offset": 0, "frame": "local", "rectangle": [-1000, -1000, 1000, 1000]}},
    "Elevation/Terrain.glb": {"family": "host-ready", "sha256": "cc", "mesh": {"up_axis": "y"}},
    "Basemap/Buildings.glb": {"family": "host-ready", "sha256": "dd", "mesh": {"up_axis": "y"}, "odbl_role": "produced-work",
        "license": "ODbL-1.0", "sources": [{"name": "OpenStreetMap", "license": "ODbL-1.0"}]},
    "Basemap/Roads.csv": {"family": "host-ready", "sha256": "ee", "count": {"kind": "rows", "value": 12}},
    "Elevation/Slope.tif": {"family": "georeferenced", "sha256": "ff"}
  },
  "not_held": %s
})JSON"), *LocalUnit, *ElevationRaster, *NotHeld);
}

/** The read of a bundle that holds every catalog file, with `Text` as its Manifest.json. */
FMantlePlaceBundleRead ReadOf(const FString& Text, const FString& Root = FString())
{
	TArray<FString> Names = { Root + TEXT("Manifest.json"), Root + TEXT("Elevation/Elevation.png"),
		Root + TEXT("Imagery/Imagery.png"), Root + TEXT("Elevation/Terrain.glb"), Root + TEXT("Basemap/Buildings.glb"),
		Root + TEXT("Basemap/Roads.csv"), Root + TEXT("Elevation/Slope.tif") };
	return FMantlePlaceBundleReaderLogic::Read(Names, [&Text](const FString& Name, FString& OutText)
	{
		OutText = Text;
		return Name.EndsWith(TEXT("Manifest.json"));
	});
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceManifestThreeLogicTest,
	"MantlePlace.Import.ManifestThreeLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceManifestThreeLogicTest::RunTest(const FString& Parameters)
{
	// --- The engine's layout for a stated size -----------------------------------------------------
	{
		FMantlePlaceLandscapeLayout L;
		TestTrue(TEXT("1009 x 1009 takes a layout"), FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(1009, 1009, L));
		TestEqual(TEXT("63-quad sections first"), L.SectionSizeQuads, 63);
		TestEqual(TEXT("two sections first"), L.SectionsPerComponent, 2);
		TestEqual(TEXT("eight components a side"), L.ComponentCountX, 8);
		TestTrue(TEXT("505 x 253 takes a layout"), FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(505, 253, L));
		TestEqual(TEXT("505 wide is four components"), L.ComponentCountX, 4);
		TestEqual(TEXT("253 high is two components"), L.ComponentCountY, 2);
		TestFalse(TEXT("1000 is a size no layout takes"), FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(1000, 1000, L));
		TestFalse(TEXT("more than 32 components a side is refused"),
			FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(7 * 33 + 1, 8, L));
	}

	// --- A full read, inside a wrapper folder ------------------------------------------------------
	{
		const FString Text = Manifest();
		const FMantlePlaceBundleRead Read = ReadOf(Text, TEXT("Bundle/"));
		TestTrue(TEXT("the 3.0 reader read it"), Read.IsRead());
		FMantlePlaceVaultManifest M;
		FString Error;
		TestTrue(TEXT("a placeable 3.0 manifest converts"), FMantlePlaceManifestThreeLogic::ToVaultManifest(Read, Text, M, Error));
		TestTrue(TEXT("valid"), M.bValid);
		TestTrue(TEXT("marked 3.x"), M.bFormatThree);
		TestEqual(TEXT("the order is the identity"), M.OrderId, FString(TEXT("order-609")));
		TestEqual(TEXT("the origin's CRS is the local frame's grid"), M.Georef.CrsProjected, FString(TEXT("EPSG:32614")));
		TestEqual(TEXT("the origin's easting"), M.Georef.OriginEastingM, 500000.0);
		TestEqual(TEXT("the origin's height is the mesh ground"), M.Georef.GroundOrthometricHM, 100.0);
		TestEqual(TEXT("the site datum, verbatim"), M.SiteVerticalDatum, FString(TEXT("EPSG:3855")));

		// The heightmap: stated numbers in centimetres, nothing else.
		const FMantlePlaceHeightmapBlock& H = M.Heightmap;
		TestTrue(TEXT("the landscape is fed"), H.bPresent);
		TestEqual(TEXT("found at its archive entry"), H.Path, FString(TEXT("Bundle/Elevation/Elevation.png")));
		TestEqual(TEXT("its digest"), H.Sha256, FString(TEXT("aa")));
		TestEqual(TEXT("resolution is the width"), H.Resolution, 1009);
		TestEqual(TEXT("components east"), H.ComponentCountX, 8);
		TestEqual(TEXT("components north"), H.ComponentCountY, 4);
		TestEqual(TEXT("cell size east in cm"), H.ScaleXPercent, 200.0);
		TestEqual(TEXT("128 units per cm of step"), H.ScaleZPercent, 0.01 * 100.0 * 128.0);
		TestEqual(TEXT("code 32768 sits at site height + offset + 32768 x scale"),
			H.LocationZOffsetCm, (100.0 - 12.5 + 327.68) * 100.0, 1e-6);
		TestEqual(TEXT("code 0 is the floor"), H.ZMinM, 87.5, 1e-9);
		TestEqual(TEXT("the grid's centre east"), H.GridCentreEastM, 0.0);
		TestEqual(TEXT("the grid's centre north"), H.GridCentreNorthM, 8.0);
		TestEqual(TEXT("the resolution check holds"), H.ComponentCountX * H.GetQuadsPerComponent() + 1, H.Resolution);

		// The spawn corner is the stated rectangle's centre less half the span, in UE order (X north).
		const FVector Spawn = M.GetLandscapeSpawnLocation();
		const FVector2D Span = M.GetAoiSizeUeCm();
		TestEqual(TEXT("north span is 4 components of 126 quads at 2 m"), Span.X, 4.0 * 126.0 * 200.0);
		TestEqual(TEXT("spawn north"), Spawn.X, 800.0 - Span.X / 2.0, 1e-6);
		TestEqual(TEXT("spawn east"), Spawn.Y, -Span.Y / 2.0, 1e-6);

		// The drape, in the origin's grid as a 2.x block holds it.
		TestTrue(TEXT("the drape is fed"), M.Drape.bPresent);
		TestEqual(TEXT("drape left"), M.Drape.LeftM, 499000.0);
		TestEqual(TEXT("drape top"), M.Drape.TopM, 4001000.0);
		TestTrue(TEXT("the terrain mesh is fed"), M.Mesh.bPresent);
		TestEqual(TEXT("its up axis"), M.Mesh.UpAxis, FString(TEXT("y")));
		TestTrue(TEXT("the buildings are fed"), M.Buildings.bPresent);
		TestEqual(TEXT("buildings path"), M.Buildings.Path, FString(TEXT("Bundle/Basemap/Buildings.glb")));

		// Terms, by archive entry.
		const FMantlePlaceFileLicensing* Terms = M.FileLicensing.Find(TEXT("Bundle/Basemap/Buildings.glb"));
		if (TestNotNull(TEXT("the buildings' terms are read"), Terms))
		{
			TestTrue(TEXT("its role"), Terms->Role == EMantlePlaceOdblRole::ProducedWork);
			TestEqual(TEXT("its source"), Terms->Sources.Num(), 1);
		}
		const FMantlePlaceFileLicensing* Unstated = M.FileLicensing.Find(TEXT("Bundle/Elevation/Terrain.glb"));
		TestTrue(TEXT("a missing role reads as an ODbL database"),
			Unstated != nullptr && Unstated->Role == EMantlePlaceOdblRole::Database);

		// Every step it cannot feed is skipped with its reason.
		const FString* TreesSkip = M.FormatSkips.Find(TEXT("foliage_points"));
		TestTrue(TEXT("trees not held show the stated reason verbatim"),
			TreesSkip != nullptr && TreesSkip->Contains(TEXT("The AOI has no tree cover.")));
		TestFalse(TEXT("trees not held are not fed"), M.Foliage.bPresent);
		const FString* Coverage = M.FormatSkips.Find(TEXT("coverage_rasters"));
		TestTrue(TEXT("coverage rasters: no host-ready file"), Coverage != nullptr && Coverage->Contains(TEXT("never converts")));
		TestTrue(TEXT("road splines skipped with a reason"), M.FormatSkips.Contains(TEXT("road_splines")));
		TestTrue(TEXT("public records skipped with a reason"), M.FormatSkips.Contains(TEXT("public_records")));
		TestFalse(TEXT("the landscape is not skipped"), M.FormatSkips.Contains(MantlePlaceArtifactId::Landscape()));
		TestFalse(TEXT("no Cesium terrain is claimed"), M.Cesium.bHasTerrain);

		// In every import mode, each step is planned or listed Not Run with its reason; the log says the same.
		const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
		for (const EMantlePlaceImportMode Mode : { EMantlePlaceImportMode::Landscape, EMantlePlaceImportMode::Mesh, EMantlePlaceImportMode::Both })
		{
			const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::FromMode(Mode, false);
			const FString Label = FString::Printf(TEXT("mode %d: "), static_cast<int32>(Mode));
			FMantlePlaceImportSteps Steps;
			MantlePlaceImportOrchestration::PlanSteps(Handlers, M, Selection, Steps);
			TestEqual(Label + TEXT("nothing-to-import passes"),
				MantlePlaceImportOrchestration::ExplainNothingToImport(Handlers, M, Selection), FString());
			for (const FMantlePlaceImportStep& Step : Steps.Steps())
			{
				TestTrue(Label + Step.Id.ToString() + TEXT(" is planned, or Not Run with a reason"),
					Step.State == EMantlePlaceImportStepState::Waiting
						|| (Step.State == EMantlePlaceImportStepState::NotRun && !Step.Reason.IsEmpty()));
			}
			const int32 RecordsRow = Steps.Find(TEXT("public_records"));
			TestTrue(Label + TEXT("public records are listed Not Run"), RecordsRow != INDEX_NONE
				&& Steps.Steps()[RecordsRow].State == EMantlePlaceImportStepState::NotRun);
			TestTrue(Label + TEXT("the landscape is planned only when the mode asks for it"),
				(Steps.Find(MantlePlaceArtifactId::Landscape()) != INDEX_NONE) == (Mode != EMantlePlaceImportMode::Mesh));
			TestTrue(Label + TEXT("the mesh is planned only when the mode asks for it"),
				(Steps.Find(MantlePlaceArtifactId::TerrainMesh()) != INDEX_NONE) == (Mode != EMantlePlaceImportMode::Landscape));
			const TArray<FString> Lines = MantlePlaceImportOrchestration::DescribeFormatSkips(Handlers, M, Selection);
			TestTrue(Label + TEXT("the log names the skipped trees"), Lines.ContainsByPredicate(
				[](const FString& Line) { return Line.StartsWith(TEXT("Skipped")) && Line.Contains(TEXT("tree cover")); }));
		}
	}

	// --- Trees in the local site frame ------------------------------------------------------------
	{
		const FString Text = Manifest(TEXT("{\"name\": \"metre\", \"to_metres\": 1}"),
			TEXT("\"width\": 1009, \"height\": 1009, \"data_type\": \"UInt16\", \"scale\": 0.01, \"offset\": 0,"
				" \"bands\": [\"height\"], \"frame\": \"local\", \"rectangle\": [-1008, -1008, 1008, 1008], \"cell_size\": [2, 2]"),
			TEXT("[]"));
		FString WithTrees = Text.Replace(TEXT("\"Elevation/Slope.tif\""),
			TEXT("\"Landcover/Trees.csv\": {\"family\": \"host-ready\", \"sha256\": \"ab\", \"count\": {\"kind\": \"rows\", \"value\": 2},"
				" \"table\": {\"columns\": [\"x\"], \"foliage_types\": {\"vocabulary\": \"mp-foliage-1\", \"values\": [\"tree\"]}}},"
				" \"Elevation/Slope.tif\""));
		TArray<FString> Names = { TEXT("Manifest.json"), TEXT("Landcover/Trees.csv") };
		const FMantlePlaceBundleRead Read = FMantlePlaceBundleReaderLogic::Read(Names, [&WithTrees](const FString& Name, FString& OutText)
		{
			OutText = WithTrees;
			return Name == TEXT("Manifest.json");
		});
		FMantlePlaceVaultManifest M;
		FString Error;
		TestTrue(TEXT("trees: converts"), FMantlePlaceManifestThreeLogic::ToVaultManifest(Read, WithTrees, M, Error));
		TestTrue(TEXT("trees are fed"), M.Foliage.bPresent);
		TestTrue(TEXT("in the site frame"), M.Foliage.bSiteFrame);
		TestEqual(TEXT("the stated count"), M.Foliage.Count, 2);
		TestEqual(TEXT("the vocabulary, verbatim"), M.Foliage.FoliageTypeVocabulary, FString(TEXT("mp-foliage-1")));
		TestEqual(TEXT("the site height to add"), M.Foliage.SiteHeightM, 100.0);
		TestTrue(TEXT("a file the download lacks is skipped as missing"),
			M.FormatSkips.Contains(MantlePlaceArtifactId::Landscape()) && !M.Heightmap.bPresent);

		// The reader the import calls takes the stated frame as this host's and offsets as they are.
		TArray<FMantlePlaceTreePointRow> Rows;
		FString TreesError;
		const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
			TEXT("x,y,ground_z,height_m,crown_radius_m,foliage_type\n10,20,1.5,12,3,tree\n-5,-6,,8,2,shrub\n"),
			0.0, 0.0, M.GetFoliagePointsFrame(), FVector2D(201600.0, 201600.0), M.Foliage.Count, Rows, TreesError,
			M.Foliage.FoliageTypeVocabulary);
		TestTrue(TEXT("Trees.csv parses in the site frame: ") + TreesError, Outcome == EMantlePlaceTreePointsOutcome::Parsed);
		if (Rows.Num() == 2)
		{
			TestEqual(TEXT("north is +X"), Rows[0].Position.X, 2000.0);
			TestEqual(TEXT("east is +Y"), Rows[0].Position.Y, 1000.0);
		}
	}

	// --- Fail closed on a unit it does not place --------------------------------------------------
	{
		const FString Text = Manifest(TEXT("{\"name\": \"US survey foot\", \"to_metres\": 0.3048006096}"));
		FMantlePlaceVaultManifest M;
		FString Error;
		TestFalse(TEXT("a site frame not in metres is refused"),
			FMantlePlaceManifestThreeLogic::ToVaultManifest(ReadOf(Text), Text, M, Error));
		TestTrue(TEXT("naming the unit: ") + Error, Error.Contains(TEXT("US survey foot")));
		TestFalse(TEXT("and is not valid"), M.bValid);
	}

	// --- A raster that states no rectangle is a named skip, never a guess -------------------------
	{
		const FString Text = Manifest(TEXT("{\"name\": \"metre\", \"to_metres\": 1}"),
			TEXT("\"width\": 1009, \"height\": 1009, \"data_type\": \"UInt16\", \"scale\": 0.01, \"offset\": 0,"
				" \"bands\": [\"height\"], \"frame\": \"local\""));
		FMantlePlaceVaultManifest M;
		FString Error;
		TestTrue(TEXT("still converts"), FMantlePlaceManifestThreeLogic::ToVaultManifest(ReadOf(Text), Text, M, Error));
		TestFalse(TEXT("no landscape from an unplaced heightmap"), M.Heightmap.bPresent);
		const FString* Reason = M.FormatSkips.Find(MantlePlaceArtifactId::Landscape());
		TestTrue(TEXT("the landscape is skipped naming the missing rectangle"),
			Reason != nullptr && Reason->Contains(TEXT("rectangle")));
		TestTrue(TEXT("the mesh still imports"), M.Mesh.bPresent);
	}

	// --- A 2.x grid is centred on the origin, as before -------------------------------------------
	{
		FMantlePlaceVaultManifest Two;
		Two.Heightmap.SectionSizeQuads = 63;
		Two.Heightmap.SectionsPerComponent = 2;
		Two.Heightmap.ComponentCountX = 4;
		Two.Heightmap.ComponentCountY = 4;
		Two.Heightmap.ScaleXPercent = 100.0;
		Two.Heightmap.ScaleYPercent = 100.0;
		const FVector2D Span = Two.GetAoiSizeUeCm();
		TestEqual(TEXT("2.x spawn north is minus half the span"), Two.GetLandscapeSpawnLocation().X, -Span.X / 2.0);
		FVector2D Scale, Offset;
		Two.Drape.LeftM = -252.0;
		Two.Drape.RightM = 252.0;
		Two.Drape.BottomM = -252.0;
		Two.Drape.TopM = 252.0;
		Two.GetDrapeUvTransform(Scale, Offset);
		TestEqual(TEXT("2.x drape covering the AOI is the identity"), Offset.X, 0.0, 1e-9);
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
