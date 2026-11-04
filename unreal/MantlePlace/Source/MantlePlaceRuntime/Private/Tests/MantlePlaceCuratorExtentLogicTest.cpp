// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCuratorExtentLogic.h"

// Open in curator opens https://mantle.place/curator#aoi=W,S,E,N&from=unreal on the extent the level
// already knows. Four sources can supply it, tried in a fixed order: a previous Mantle Place
// import's provenance, the level's native georeferencing, a Cesium georeference, an Extent box. Each
// source is a pure function from level state to extent, and the order is Resolve. The engine reads
// that fill the state are the editor module's, asserted in MantlePlace.CuratorExtent.LevelRead.

namespace
{
	using FLogic = FMantlePlaceCuratorExtentLogic;

	FMantlePlaceExtent Extent(double West, double South, double East, double North)
	{
		FMantlePlaceExtent Result;
		Result.West = West;
		Result.South = South;
		Result.East = East;
		Result.North = North;
		return Result;
	}

	FMantlePlaceGeoPoint Point(double Longitude, double Latitude)
	{
		FMantlePlaceGeoPoint Result;
		Result.Longitude = Longitude;
		Result.Latitude = Latitude;
		return Result;
	}

	FMantlePlaceExtentBoxState Box(double CenterLongitude, double CenterLatitude, double WidthM, double DepthM)
	{
		FMantlePlaceExtentBoxState Result;
		Result.CenterLongitude = CenterLongitude;
		Result.CenterLatitude = CenterLatitude;
		Result.WidthM = WidthM;
		Result.DepthM = DepthM;
		return Result;
	}

	/** The four corners of a level whose bounds a georeference placed over the given degrees. */
	TArray<FMantlePlaceGeoPoint> Corners(double West, double South, double East, double North)
	{
		return { Point(West, South), Point(East, South), Point(East, North), Point(West, North) };
	}

	/** A level that holds every source, each over a different place, so the winner is unmistakable. */
	FMantlePlaceLevelState EveryLevelSource()
	{
		FMantlePlaceLevelState Level;
		Level.ImportExtents = { Extent(-97.75, 30.25, -97.73, 30.27) };
		Level.NativeGeoreferencedPoints = Corners(-96.0, 31.0, -95.9, 31.1);
		Level.CesiumGeoreferencedPoints = Corners(-95.0, 32.0, -94.9, 32.1);
		Level.ExtentBoxes = { Box(-94.0, 33.0, 1000.0, 1000.0) };
		return Level;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCuratorExtentSourcesTest,
    "MantlePlace.CuratorExtent.Sources",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCuratorExtentSourcesTest::RunTest(const FString& Parameters)
{
	// --- Source 1: a previous import's provenance ------------------------------------------------
	{
		FMantlePlaceExtent Out;
		TestTrue(TEXT("one import's extent is read"), FLogic::FromPriorImports({ Extent(-97.75, 30.25, -97.73, 30.27) }, Out));
		TestEqual(TEXT("west"), Out.West, -97.75);
		TestEqual(TEXT("south"), Out.South, 30.25);
		TestEqual(TEXT("east"), Out.East, -97.73);
		TestEqual(TEXT("north"), Out.North, 30.27);

		TestTrue(TEXT("two imports in one level give the union"),
			FLogic::FromPriorImports({ Extent(-97.75, 30.25, -97.73, 30.27), Extent(-97.70, 30.20, -97.60, 30.30) }, Out));
		TestEqual(TEXT("union west"), Out.West, -97.75);
		TestEqual(TEXT("union south"), Out.South, 30.20);
		TestEqual(TEXT("union east"), Out.East, -97.60);
		TestEqual(TEXT("union north"), Out.North, 30.30);

		TestTrue(TEXT("an unusable record is skipped, the usable one is read"),
			FLogic::FromPriorImports({ Extent(0, 0, 0, 0), Extent(-97.75, 30.25, -97.73, 30.27) }, Out));
		TestEqual(TEXT("the usable record alone"), Out.West, -97.75);

		TestFalse(TEXT("no import records: no extent"), FLogic::FromPriorImports({}, Out));
		TestFalse(TEXT("an inverted record is not an extent"), FLogic::FromPriorImports({ Extent(-97.73, 30.25, -97.75, 30.27) }, Out));
		TestFalse(TEXT("a record outside the world is not an extent"), FLogic::FromPriorImports({ Extent(-200, 30, -190, 31) }, Out));
		TestFalse(TEXT("a NaN record is not an extent"),
			FLogic::FromPriorImports({ Extent(NAN, 30.25, -97.73, 30.27) }, Out));
	}

	// --- Sources 2 and 3: a georeference converts the level's bounds to degrees ------------------
	{
		FMantlePlaceExtent Out;
		const TArray<FMantlePlaceGeoPoint> Level = Corners(-97.75, 30.25, -97.73, 30.27);
		TestTrue(TEXT("native georeference: corners give their box"), FLogic::FromNativeGeoreference(Level, Out));
		TestEqual(TEXT("native west"), Out.West, -97.75);
		TestEqual(TEXT("native north"), Out.North, 30.27);
		TestTrue(TEXT("Cesium georeference: corners give their box"), FLogic::FromCesiumGeoreference(Level, Out));
		TestEqual(TEXT("Cesium south"), Out.South, 30.25);
		TestEqual(TEXT("Cesium east"), Out.East, -97.73);

		// A georeference can rotate the level against north, so the box is the box of ALL corners.
		TestTrue(TEXT("a rotated level boxes every corner"),
			FLogic::FromNativeGeoreference({ Point(-97.74, 30.25), Point(-97.73, 30.26), Point(-97.74, 30.27), Point(-97.75, 30.26) }, Out));
		TestEqual(TEXT("rotated west"), Out.West, -97.75);
		TestEqual(TEXT("rotated east"), Out.East, -97.73);
		TestEqual(TEXT("rotated south"), Out.South, 30.25);
		TestEqual(TEXT("rotated north"), Out.North, 30.27);

		TestFalse(TEXT("no points: no extent"), FLogic::FromNativeGeoreference({}, Out));
		TestFalse(TEXT("one point has no area"), FLogic::FromNativeGeoreference({ Point(-97.74, 30.26) }, Out));
		TestFalse(TEXT("an empty level has no area"), FLogic::FromCesiumGeoreference(Corners(-97.74, 30.26, -97.74, 30.26), Out));
		TestFalse(TEXT("a broken georeference (NaN) gives no partial extent"),
			FLogic::FromNativeGeoreference({ Point(-97.75, 30.25), Point(NAN, NAN), Point(-97.73, 30.27) }, Out));
		TestFalse(TEXT("a point off the globe gives no extent"),
			FLogic::FromCesiumGeoreference({ Point(-97.75, 30.25), Point(-97.73, 91.0) }, Out));
		TestFalse(TEXT("a level across the antimeridian is refused, not drawn the wrong way round"),
			FLogic::FromNativeGeoreference({ Point(179.99, 10.0), Point(-179.99, 10.1) }, Out));
	}

	// --- Source 4: the Extent box ---------------------------------------------------------------
	{
		FMantlePlaceExtent Out;
		TestTrue(TEXT("a located 1 km box reads"), FLogic::FromExtentBox(Box(-97.7431, 30.2672, 1000.0, 1000.0), Out));
		TestTrue(TEXT("the extent is valid W,S,E,N degrees"), Out.IsValid());
		TestEqual(TEXT("centred east-west"), (Out.West + Out.East) * 0.5, -97.7431, 1e-9);
		TestEqual(TEXT("centred north-south"), (Out.South + Out.North) * 0.5, 30.2672, 1e-9);
		// A kilometre is about 0.00902 degrees of latitude and 0.01039 of longitude at 30.27 N.
		TestEqual(TEXT("1 km is about 0.00902 degrees of latitude"), Out.North - Out.South, 0.00902, 0.00005);
		TestEqual(TEXT("1 km is about 0.01039 degrees of longitude here"), Out.East - Out.West, 0.01039, 0.00005);

		FMantlePlaceExtent Wide;
		TestTrue(TEXT("scale reads: a box twice as wide"), FLogic::FromExtentBox(Box(-97.7431, 30.2672, 2000.0, 1000.0), Wide));
		TestEqual(TEXT("twice the longitude span"), Wide.East - Wide.West, 2.0 * (Out.East - Out.West), 1e-9);
		TestEqual(TEXT("the same latitude span"), Wide.North - Wide.South, Out.North - Out.South, 1e-9);

		FMantlePlaceExtent Far;
		TestTrue(TEXT("a box at 60 N reads"), FLogic::FromExtentBox(Box(10.0, 60.0, 1000.0, 1000.0), Far));
		TestTrue(TEXT("a kilometre is more degrees of longitude the further north"), (Far.East - Far.West) > (Out.East - Out.West));

		TestTrue(TEXT("two boxes give the union"),
			FLogic::FromExtentBoxes({ Box(-97.7431, 30.2672, 1000.0, 1000.0), Box(-97.70, 30.30, 1000.0, 1000.0) }, Out));
		TestTrue(TEXT("the union spans both centres"), Out.West < -97.7431 && Out.East > -97.70 && Out.South < 30.2672 && Out.North > 30.30);

		TestFalse(TEXT("no box: no extent"), FLogic::FromExtentBoxes({}, Out));
		TestFalse(TEXT("a box nobody located (0,0) is refused"), FLogic::FromExtentBox(Box(0.0, 0.0, 1000.0, 1000.0), Out));
		TestFalse(TEXT("a box with no width is refused"), FLogic::FromExtentBox(Box(-97.7, 30.2, 0.0, 1000.0), Out));
		TestFalse(TEXT("a box with no depth is refused"), FLogic::FromExtentBox(Box(-97.7, 30.2, 1000.0, -5.0), Out));
		TestFalse(TEXT("a NaN size is refused"), FLogic::FromExtentBox(Box(-97.7, 30.2, NAN, 1000.0), Out));
		TestFalse(TEXT("a centre off the globe is refused"), FLogic::FromExtentBox(Box(-97.7, 95.0, 1000.0, 1000.0), Out));
		TestFalse(TEXT("a box at the pole is refused"), FLogic::FromExtentBox(Box(10.0, 89.95, 1000.0, 1000.0), Out));
		TestFalse(TEXT("a box over the antimeridian is refused, not clipped"), FLogic::FromExtentBox(Box(179.999, 10.0, 5000.0, 1000.0), Out));
		TestTrue(TEXT("an unreadable box is skipped, the readable one is used"),
			FLogic::FromExtentBoxes({ Box(0.0, 0.0, 1000.0, 1000.0), Box(-97.7431, 30.2672, 1000.0, 1000.0) }, Out));
		TestEqual(TEXT("it is the readable box's centre"), (Out.West + Out.East) * 0.5, -97.7431, 1e-9);
	}

	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCuratorExtentPrecedenceTest,
    "MantlePlace.CuratorExtent.Precedence",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCuratorExtentPrecedenceTest::RunTest(const FString& Parameters)
{
	// Order: prior import, native georeference, Cesium georeference, Extent box. Take the winner
	// away and the next one wins, down to nothing.
	FMantlePlaceLevelState Level = EveryLevelSource();

	FMantlePlaceResolvedExtent Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("a previous import wins over all three"), Resolved.Source == EMantlePlaceExtentSource::PriorImport);
	TestEqual(TEXT("and its extent is the one used"), Resolved.Extent.West, -97.75);

	Level.ImportExtents.Reset();
	Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("then the native georeference"), Resolved.Source == EMantlePlaceExtentSource::NativeGeoreference);
	TestEqual(TEXT("with its extent"), Resolved.Extent.West, -96.0);

	Level.NativeGeoreferencedPoints.Reset();
	Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("then the Cesium georeference"), Resolved.Source == EMantlePlaceExtentSource::CesiumGeoreference);
	TestEqual(TEXT("with its extent"), Resolved.Extent.West, -95.0);

	Level.CesiumGeoreferencedPoints.Reset();
	Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("then the Extent box"), Resolved.Source == EMantlePlaceExtentSource::ExtentBox);
	TestTrue(TEXT("with its extent"), Resolved.Extent.West < -94.0 && Resolved.Extent.East > -94.0);

	Level.ExtentBoxes.Reset();
	Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("and with none, there is no source"), Resolved.Source == EMantlePlaceExtentSource::None);
	TestFalse(TEXT("which is not set"), Resolved.IsSet());

	// A source that is present but unreadable does not stop the next one.
	Level = EveryLevelSource();
	Level.ImportExtents = { Extent(0, 0, 0, 0) };
	Level.NativeGeoreferencedPoints = { Point(NAN, NAN), Point(NAN, NAN) };
	Resolved = FLogic::Resolve(Level);
	TestTrue(TEXT("unreadable sources are skipped, not fatal"), Resolved.Source == EMantlePlaceExtentSource::CesiumGeoreference);

	// An empty level, and a level with only an unlocated box, offer the plain curator.
	TestTrue(TEXT("an empty level has no source"), FLogic::Resolve(FMantlePlaceLevelState()).Source == EMantlePlaceExtentSource::None);
	FMantlePlaceLevelState Unlocated;
	Unlocated.ExtentBoxes = { Box(0.0, 0.0, 1000.0, 1000.0) };
	TestTrue(TEXT("a box nobody located is no source"), FLogic::Resolve(Unlocated).Source == EMantlePlaceExtentSource::None);

	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCuratorExtentUrlTest,
    "MantlePlace.CuratorExtent.Url",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCuratorExtentUrlTest::RunTest(const FString& Parameters)
{
	const FString Base = TEXT("https://mantle.place");

	FMantlePlaceResolvedExtent Resolved;
	Resolved.Source = EMantlePlaceExtentSource::PriorImport;
	Resolved.Extent = Extent(-97.75, 30.25, -97.73, 30.27);
	TestEqual(TEXT("the link carries the extent in W,S,E,N and from=unreal"),
		FLogic::BuildCuratorUrl(Base, Resolved), FString(TEXT("https://mantle.place/curator#aoi=-97.75,30.25,-97.73,30.27&from=unreal")));
	TestEqual(TEXT("a trailing slash on the base is dropped"),
		FLogic::BuildCuratorUrl(TEXT("https://mantle.place/"), Resolved), FString(TEXT("https://mantle.place/curator#aoi=-97.75,30.25,-97.73,30.27&from=unreal")));

	// The curator keeps 4 decimals; the link rounds OUTWARD so the drawn extent contains the level's.
	Resolved.Extent = Extent(-97.74311, 30.25001, -97.73001, 30.26001);
	TestEqual(TEXT("bounds round outward to four decimals"),
		FLogic::BuildCuratorUrl(Base, Resolved), FString(TEXT("https://mantle.place/curator#aoi=-97.7432,30.25,-97.73,30.2601&from=unreal")));

	Resolved.Extent = Extent(-98.0, 10.0, -97.5, 10.5);
	TestEqual(TEXT("whole numbers print without a point, trailing zeros are trimmed"),
		FLogic::BuildCuratorUrl(Base, Resolved), FString(TEXT("https://mantle.place/curator#aoi=-98,10,-97.5,10.5&from=unreal")));

	Resolved.Extent = Extent(-0.5, -0.00001, 0.00001, 0.5);
	TestEqual(TEXT("a bound between 0 and -1 keeps its sign"),
		FLogic::BuildCuratorUrl(Base, Resolved), FString(TEXT("https://mantle.place/curator#aoi=-0.5,-0.0001,0.0001,0.5&from=unreal")));

	TestEqual(TEXT("a level with no source opens the plain curator"),
		FLogic::BuildCuratorUrl(Base, FMantlePlaceResolvedExtent()), FString(TEXT("https://mantle.place/curator")));

	FMantlePlaceResolvedExtent Broken;
	Broken.Source = EMantlePlaceExtentSource::ExtentBox;
	Broken.Extent = Extent(-97.73, 30.25, -97.75, 30.27);
	TestEqual(TEXT("an invalid extent is never sent: the plain curator opens"),
		FLogic::BuildCuratorUrl(Base, Broken), FString(TEXT("https://mantle.place/curator")));

	TestTrue(TEXT("a base that is not http(s) opens nothing"), FLogic::BuildCuratorUrl(TEXT("ftp://mantle.place"), Resolved).IsEmpty());
	TestTrue(TEXT("an empty base opens nothing"), FLogic::BuildCuratorUrl(FString(), Resolved).IsEmpty());

	TestEqual(TEXT("from names this host"), FString(FLogic::From()), FString(TEXT("unreal")));
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
