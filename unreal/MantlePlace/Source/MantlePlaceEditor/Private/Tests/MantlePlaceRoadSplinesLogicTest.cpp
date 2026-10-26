// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceRoadSplinesLogic.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"

// The WGS84 -> UTM known answers come from tools/manifest-conformance/corpus/projection/ (HPS-40,
// HPS-45). Projection is the one place a host re-derives a number the ETL already computed, so it
// is the one place a host can silently misplace a customer's site by kilometres — a dropped false
// northing south of the equator puts the AOI 10 000 km away and nothing else notices.
//
// The GeoJSON -> spline assertions below stay inline: FMantlePlaceRoadSpline is this plugin's own
// structure and no other host produces it.

namespace
{
using namespace MantlePlaceConformanceCorpus;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceRoadSplinesLogicTest,
    "MantlePlace.Import.RoadSplinesLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceRoadSplinesLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceRoadSplinesLogic;

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("projection"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	TSet<FString> Driven;

	// The AOI origin the inline GeoJSON section below is expressed in; filled from the corpus so
	// there is exactly one place the numbers live.
	double OriginLonDeg = 0.0, OriginLatDeg = 0.0, OriginEastingM = 0.0, OriginNorthingM = 0.0;
	int32 Epsg = 0;
	bool bHaveOrigin = false;

	if (const FCase* Case = FindCase(Cases, TEXT("projection.lonLatToUtm")))
	{
		Driven.Add(Case->Id);

		const double ToleranceMetres = Case->PayloadObject.IsValid()
			? RowNumber(Case->PayloadObject, TEXT("toleranceMetres"), 0.05)
			: 0.05;

		const TArray<TSharedPtr<FJsonObject>> Pairs = Rows(*Case, TEXT("pairs"));
		TestTrue(Case->What(TEXT("has known-answer pairs")), Pairs.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Pairs)
		{
			const double LonDeg = RowNumber(Row, TEXT("lonDeg"));
			const double LatDeg = RowNumber(Row, TEXT("latDeg"));
			const int32 RowEpsg = static_cast<int32>(RowNumber(Row, TEXT("epsg")));
			const FString Where = FString::Printf(TEXT("[%s] (%.6f, %.6f) EPSG:%d"),
				*Case->Id, LonDeg, LatDeg, RowEpsg);

			double EastingM = 0.0, NorthingM = 0.0;
			TestTrue(Where + TEXT(" projects"), FLogic::LonLatToUtm(LonDeg, LatDeg, RowEpsg, EastingM, NorthingM));
			TestEqual(Where + TEXT(" easting"), EastingM, RowNumber(Row, TEXT("eastingM")), ToleranceMetres);
			TestEqual(Where + TEXT(" northing"), NorthingM, RowNumber(Row, TEXT("northingM")), ToleranceMetres);

			if (!bHaveOrigin)
			{
				OriginLonDeg = LonDeg;
				OriginLatDeg = LatDeg;
				OriginEastingM = RowNumber(Row, TEXT("eastingM"));
				OriginNorthingM = RowNumber(Row, TEXT("northingM"));
				Epsg = RowEpsg;
				bHaveOrigin = true;
			}
		}

		// South of the equator the 10 000 km false northing must be applied.
		for (const TSharedPtr<FJsonObject>& Row : Rows(*Case, TEXT("southernHemisphere")))
		{
			const int32 RowEpsg = static_cast<int32>(RowNumber(Row, TEXT("epsg")));
			double EastingM = 0.0, NorthingM = 0.0;
			const FString Where = FString::Printf(TEXT("[%s] southern EPSG:%d"), *Case->Id, RowEpsg);

			TestTrue(Where + TEXT(" projects"),
				FLogic::LonLatToUtm(RowNumber(Row, TEXT("lonDeg")), RowNumber(Row, TEXT("latDeg")),
					RowEpsg, EastingM, NorthingM));
			TestTrue(Where + TEXT(" carries the false northing"),
				NorthingM > RowNumber(Row, TEXT("northingGreaterThan")));
		}

		// Rejects: a non-UTM EPSG, and a latitude outside the UTM validity band. A row states only
		// what it varies, so the rest falls back to the (valid) origin.
		for (const TSharedPtr<FJsonObject>& Row : Rows(*Case, TEXT("rejects")))
		{
			const double LonDeg = RowNumber(Row, TEXT("lonDeg"), OriginLonDeg);
			const double LatDeg = RowNumber(Row, TEXT("latDeg"), OriginLatDeg);
			const int32 RowEpsg = static_cast<int32>(RowNumber(Row, TEXT("epsg"), Epsg));

			double EastingM = 0.0, NorthingM = 0.0;
			TestFalse(
				FString::Printf(TEXT("[%s] rejected: %s"), *Case->Id, *RowString(Row, TEXT("reason"))),
				FLogic::LonLatToUtm(LonDeg, LatDeg, RowEpsg, EastingM, NorthingM));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the projection group but nothing here drives it (HPS-41)"),
			*Missing));
	}

	if (!bHaveOrigin)
	{
		AddError(TEXT("the projection corpus supplied no origin pair; cannot exercise ParseGeoJson"));
		return false;
	}

	// --- ParseGeoJson: LineString + MultiLineString + properties -> Local Projected Frame ------
	{
		const FString GeoJson = FString::Printf(LR"JSON(
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "properties": { "class": "residential", "name": "Comet Rd", "width_m_estimated": 6.0, "z_datum": "EGM2008-orthometric" },
      "geometry": { "type": "LineString", "coordinates": [[%.14f, %.14f, 2640.0], [%.14f, %.14f, 2641.0]] }
    },
    {
      "type": "Feature",
      "properties": { "class": "track", "width_m_estimated": 3.5 },
      "geometry": { "type": "MultiLineString", "coordinates": [
        [[%.14f, %.14f, 2650.0], [%.14f, %.14f, 2651.0]],
        [[%.14f, %.14f, 2652.0], [%.14f, %.14f, 2653.0]]
      ] }
    },
    { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [%.14f, %.14f] } }
  ]
}
)JSON",
		                                        OriginLonDeg, OriginLatDeg, OriginLonDeg + 0.001, OriginLatDeg,
		                                        OriginLonDeg, OriginLatDeg, OriginLonDeg, OriginLatDeg + 0.001,
		                                        OriginLonDeg, OriginLatDeg, OriginLonDeg - 0.001, OriginLatDeg,
		                                        OriginLonDeg, OriginLatDeg);

		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = -1;
		FString Error;
		TestTrue(TEXT("geojson parses"),
			FLogic::ParseGeoJson(GeoJson, OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Error));
		TestEqual(TEXT("no parse error"), Error, FString());
		TestEqual(TEXT("LineString + 2 MultiLineString parts, Point skipped"), Splines.Num(), 3);
		TestEqual(TEXT("every line part was seen, the Point was not one"), LinesSeen, 3);

		if (Splines.Num() == 3)
		{
			// First point of the first spline is the origin itself -> world (0,0) at z*100.
			TestEqual(TEXT("origin point lands at world X=0"), Splines[0].PointsUeCm[0].X, 0.0, 5.0);
			TestEqual(TEXT("origin point lands at world Y=0"), Splines[0].PointsUeCm[0].Y, 0.0, 5.0);
			TestEqual(TEXT("Z is orthometric meters -> cm"), Splines[0].PointsUeCm[0].Z, 264000.0, 1e-3);
			// UE is left-handed: North -> +X, East -> +Y. Each direction asserts BOTH components,
			// because a half-done axis swap still satisfies the one-sided form of these checks. The
			// cross-axis tolerance is deliberately loose (20 m): a due-east step in geographic space
			// does pick up a little northing through meridian convergence, but a swapped axis would
			// land at ~90 m, so 20 m separates the two without being brittle about convergence.
			//
			// +0.001 deg lon at this latitude is ~89.9 m east -> +Y, and no meaningful northing.
			TestTrue(TEXT("east of origin -> +Y"), Splines[0].PointsUeCm[1].Y > 8000.0);
			TestEqual(TEXT("east of origin has no +X"), Splines[0].PointsUeCm[1].X, 0.0, 2000.0);
			TestEqual(TEXT("width rides along"), Splines[0].WidthMEstimated, 6.0, 1e-9);
			TestEqual(TEXT("class rides along"), Splines[0].RoadClass, FString(TEXT("residential")));
			TestEqual(TEXT("name rides along"), Splines[0].Name, FString(TEXT("Comet Rd")));
			// +0.001 deg lat is ~110.9 m north -> +X, and no easting.
			TestTrue(TEXT("north of origin -> +X"), Splines[1].PointsUeCm[1].X > 10000.0);
			TestEqual(TEXT("north of origin has no +Y"), Splines[1].PointsUeCm[1].Y, 0.0, 2000.0);
			TestEqual(TEXT("second MultiLineString part is its own spline"),
				Splines[2].RoadClass, FString(TEXT("track")));
		}
	}

	// --- Fail-closed on structural problems only ----------------------------------------------
	{
		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0;
		FString Error;
		TestFalse(TEXT("invalid JSON fails"),
			FLogic::ParseGeoJson(TEXT("not json"), OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Error));
		TestFalse(TEXT("missing features fails"),
			FLogic::ParseGeoJson(TEXT("{\"type\":\"FeatureCollection\"}"),
				OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Error));
	}

	// --- A layer nothing could place is not an empty layer ------------------------------------
	// The two used to be the same log line. A non-UTM origin makes every point fail projection,
	// every spline drop to under two points, and the importer report "created (0 spline actor(s))"
	// -- indistinguishable from an AOI with no roads.
	{
		const FString OneRoad = FString::Printf(LR"JSON(
{ "type": "FeatureCollection", "features": [ {
  "type": "Feature", "properties": {},
  "geometry": { "type": "LineString", "coordinates": [[%.14f, %.14f, 0.0], [%.14f, %.14f, 0.0]] }
} ] }
)JSON",
		                                        OriginLonDeg, OriginLatDeg, OriginLonDeg + 0.001, OriginLatDeg);
		const FString NoRoads = TEXT("{\"type\":\"FeatureCollection\",\"features\":[]}");

		// Refused up front, by name, before any point is projected.
		constexpr int32 WebMercatorEpsg = 3857;
		TestFalse(TEXT("3857 is not a UTM zone"), FLogic::IsUtmEpsg(WebMercatorEpsg));
		TestFalse(TEXT("0 (no georeference block) is not a UTM zone"), FLogic::IsUtmEpsg(0));
		TestTrue(TEXT("32613 is a north UTM zone"), FLogic::IsUtmEpsg(32613));
		TestTrue(TEXT("32760 is a south UTM zone"), FLogic::IsUtmEpsg(32760));
		TestFalse(TEXT("32661 is past the last north zone"), FLogic::IsUtmEpsg(32661));

		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0;
		FString RefusedError;
		TestFalse(TEXT("non-UTM origin refuses a layer that has roads"),
			FLogic::ParseGeoJson(OneRoad, OriginEastingM, OriginNorthingM, WebMercatorEpsg, Splines, LinesSeen, RefusedError));
		TestTrue(TEXT("the refusal names the EPSG"), RefusedError.Contains(TEXT("3857")));
		TestTrue(TEXT("the refusal says why: not a UTM zone"), RefusedError.Contains(TEXT("UTM")));
		TestEqual(TEXT("nothing was seen, nothing was projected"), LinesSeen, 0);

		FString RefusedEmptyError;
		TestFalse(TEXT("non-UTM origin refuses even an empty layer: the origin is the fault"),
			FLogic::ParseGeoJson(NoRoads, OriginEastingM, OriginNorthingM, WebMercatorEpsg, Splines, LinesSeen, RefusedEmptyError));
		TestEqual(TEXT("empty layer refuses with the same reason"), RefusedEmptyError, RefusedError);
		TestTrue(TEXT("a refusal does not read as 'the layer has no roads'"),
			!RefusedError.Contains(TEXT("no roads")));

		// An empty layer is a success with zero, and knows it saw nothing.
		FString EmptyError;
		int32 EmptySeen = -1;
		TestTrue(TEXT("empty layer succeeds"),
			FLogic::ParseGeoJson(NoRoads, OriginEastingM, OriginNorthingM, Epsg, Splines, EmptySeen, EmptyError));
		TestEqual(TEXT("empty layer: zero splines"), Splines.Num(), 0);
		TestEqual(TEXT("empty layer: zero lines seen"), EmptySeen, 0);

		// Lines present but none placeable: an out-of-band latitude fails every point, so this is
		// the same shape a partly-bad layer produces, reached without a non-UTM origin.
		const FString Unplaceable = TEXT(R"JSON(
{ "type": "FeatureCollection", "features": [ {
  "type": "Feature", "properties": {},
  "geometry": { "type": "LineString", "coordinates": [[0.0, 89.0, 0.0], [0.001, 89.0, 0.0]] }
} ] }
)JSON");
		int32 UnplaceableSeen = -1;
		FString UnplaceableError;
		TestTrue(TEXT("a layer with only unplaceable lines is still structurally valid"),
			FLogic::ParseGeoJson(Unplaceable, OriginEastingM, OriginNorthingM, Epsg, Splines, UnplaceableSeen, UnplaceableError));
		TestEqual(TEXT("unplaceable: zero splines"), Splines.Num(), 0);
		TestEqual(TEXT("unplaceable: but the line was seen"), UnplaceableSeen, 1);

		// The two outcomes read differently, and the partial one names both numbers.
		const FString EmptyLine = FLogic::DescribeOutcome(0, 0);
		const FString NoneLine = FLogic::DescribeOutcome(4, 0);
		const FString PartialLine = FLogic::DescribeOutcome(4, 3);
		const FString AllLine = FLogic::DescribeOutcome(4, 4);
		TestNotEqual(TEXT("empty and all-unplaceable read differently"), EmptyLine, NoneLine);
		TestTrue(TEXT("empty says the layer had no roads"), EmptyLine.Contains(TEXT("no road")));
		TestTrue(TEXT("empty is not a warning"), !EmptyLine.Contains(TEXT("NOT placed")));
		TestTrue(TEXT("all-unplaceable says NOT placed"), NoneLine.Contains(TEXT("NOT placed")));
		TestTrue(TEXT("all-unplaceable names how many were present"), NoneLine.Contains(TEXT("4")));
		TestTrue(TEXT("partial names placed and present"),
			PartialLine.Contains(TEXT("3")) && PartialLine.Contains(TEXT("4")));
		TestTrue(TEXT("partial says the rest were dropped"), PartialLine.Contains(TEXT("dropped")));
		TestTrue(TEXT("all placed reports the created count"), AllLine.Contains(TEXT("created (4")));
	}

	// --- Which source the roads come from (MPB 1.16.0; HPS-52) -----------------------------------
	// A US order's shared layer is NAVD88 and the landscape is EGM2008, so the Unreal block's own
	// copy is read first and the shared layer stays behind its datum check. Nothing here converts a
	// height: a source is placed as the file carries it or it is refused by name.
	{
		const FString Egm = TEXT("EGM2008-orthometric");
		const FString Navd = TEXT("NAVD88 (GEOID18)");
		const FMantlePlaceRoadSplinesDatum SharedNavd{ Navd, /*bRequired*/ true };
		const FMantlePlaceRoadSplinesDatum SharedEgm{ Egm, /*bRequired*/ true };
		const FMantlePlaceRoadSplinesDatum SharedPre18{ FString(), /*bRequired*/ false };

		const auto FirstPlaceable = [](const TArray<FMantlePlaceRoadSplinesCandidate>& Plan) -> const FMantlePlaceRoadSplinesCandidate*
		{
			return Plan.FindByPredicate([](const FMantlePlaceRoadSplinesCandidate& Candidate) { return Candidate.bPlaceable; });
		};

		// A US order at 1.16.0: the copy states EGM2008, the shared layer NAVD88. The copy is placed;
		// the shared layer is passed over by name, never shifted and never placed as it is.
		{
			const TArray<FMantlePlaceRoadSplinesCandidate> Plan = FLogic::PlanSources(true, Egm, true, SharedNavd);
			if (TestEqual(TEXT("a US 1.16.0 order lists both sources"), Plan.Num(), 2))
			{
				TestTrue(TEXT("the engine copy comes first"), Plan[0].Source == EMantlePlaceRoadSplinesSource::EngineCopy);
				TestTrue(TEXT("the engine copy states this host's datum and is placeable"), Plan[0].bPlaceable);
				TestTrue(TEXT("a placeable source carries no refusal"), Plan[0].Refusal.IsEmpty());
				TestTrue(TEXT("the shared layer comes second"), Plan[1].Source == EMantlePlaceRoadSplinesSource::SharedLayer);
				TestFalse(TEXT("the shared NAVD88 layer is not placeable"), Plan[1].bPlaceable);
				TestTrue(TEXT("and its refusal names the datum it stated"), Plan[1].Refusal.Contains(Navd));
			}
			const FMantlePlaceRoadSplinesCandidate* Chosen = FirstPlaceable(Plan);
			if (TestNotNull(TEXT("the roads are placed"), Chosen))
			{
				TestTrue(TEXT("from the engine copy"), Chosen->Source == EMantlePlaceRoadSplinesSource::EngineCopy);
			}
		}

		// A US order at 1.8.0 to 1.15.0 publishes no copy: the shared layer is the only source, it
		// is NAVD88, and the order gets no roads rather than shifted or unshifted ones.
		{
			const TArray<FMantlePlaceRoadSplinesCandidate> Plan = FLogic::PlanSources(false, FString(), true, SharedNavd);
			if (TestEqual(TEXT("without the pointer only the shared layer is a source"), Plan.Num(), 1))
			{
				TestTrue(TEXT("it is the shared layer"), Plan[0].Source == EMantlePlaceRoadSplinesSource::SharedLayer);
				TestFalse(TEXT("which is not placeable"), Plan[0].bPlaceable);
				TestTrue(TEXT("and is refused by the datum it stated"), Plan[0].Refusal.Contains(Navd));
			}
			TestNull(TEXT("no road is placed"), FirstPlaceable(Plan));
		}

		// A non-US order, and a bundle from before the format stated a datum, place the shared layer
		// exactly as before.
		{
			const TArray<FMantlePlaceRoadSplinesCandidate> NonUs = FLogic::PlanSources(false, FString(), true, SharedEgm);
			const FMantlePlaceRoadSplinesCandidate* NonUsChosen = FirstPlaceable(NonUs);
			if (TestNotNull(TEXT("a non-US order places its roads"), NonUsChosen))
			{
				TestTrue(TEXT("from the shared layer"), NonUsChosen->Source == EMantlePlaceRoadSplinesSource::SharedLayer);
			}
			const TArray<FMantlePlaceRoadSplinesCandidate> Old = FLogic::PlanSources(false, FString(), true, SharedPre18);
			const FMantlePlaceRoadSplinesCandidate* OldChosen = FirstPlaceable(Old);
			if (TestNotNull(TEXT("a bundle from before 1.8.0 places its roads"), OldChosen))
			{
				TestTrue(TEXT("from the shared layer"), OldChosen->Source == EMantlePlaceRoadSplinesSource::SharedLayer);
			}
		}

		// The pointer's datum is asked, not assumed from where the pointer sits or what its file is
		// called. A copy that states another datum, none, or EGM2008 in other letters is refused by
		// name, and the shared layer is then the only one that can be placed.
		{
			const TCHAR* Wrong[] = { TEXT("NAVD88 (GEOID18)"), TEXT("a-datum-no-reader-knows"), TEXT("egm2008-orthometric"), TEXT("") };
			for (const TCHAR* Stated : Wrong)
			{
				FString Reason;
				TestFalse(*FString::Printf(TEXT("an engine copy stating \"%s\" is refused"), Stated),
					FLogic::CanPlaceEngineHeights(Stated, Reason));
				TestFalse(*FString::Printf(TEXT("\"%s\": the refusal is named"), Stated), Reason.IsEmpty());
				if (FCString::Strlen(Stated) > 0)
				{
					TestTrue(*FString::Printf(TEXT("\"%s\": the refusal names what was stated"), Stated), Reason.Contains(Stated));
				}
				else
				{
					TestTrue(TEXT("an unstated datum is named as unstated"), Reason.Contains(TEXT("states no vertical datum")));
				}

				const TArray<FMantlePlaceRoadSplinesCandidate> Plan = FLogic::PlanSources(true, Stated, true, SharedNavd);
				TestNull(*FString::Printf(TEXT("\"%s\": a refused copy and a NAVD88 layer place nothing"), Stated), FirstPlaceable(Plan));
			}
			FString Reason;
			TestTrue(TEXT("an engine copy stating EGM2008 is placeable"), FLogic::CanPlaceEngineHeights(Egm, Reason));
			TestTrue(TEXT("with no refusal"), Reason.IsEmpty());

			// The copy's refusal leaves the shared layer to be judged on its own datum.
			const TArray<FMantlePlaceRoadSplinesCandidate> Fallback = FLogic::PlanSources(true, Navd, true, SharedEgm);
			const FMantlePlaceRoadSplinesCandidate* FallbackChosen = FirstPlaceable(Fallback);
			if (TestNotNull(TEXT("a refused copy beside a placeable shared layer still places roads"), FallbackChosen))
			{
				TestTrue(TEXT("from the shared layer"), FallbackChosen->Source == EMantlePlaceRoadSplinesSource::SharedLayer);
			}
		}

		TestEqual(TEXT("a manifest declaring no road source plans none"),
			FLogic::PlanSources(false, FString(), false, SharedEgm).Num(), 0);

		// The log names where the placed roads were read from: the engine copy says so.
		const FString EngineLine = FLogic::DescribeSource(EMantlePlaceRoadSplinesSource::EngineCopy, TEXT("Vector/RoadSplinesEngine.gpkg"));
		const FString SharedLine = FLogic::DescribeSource(EMantlePlaceRoadSplinesSource::SharedLayer, TEXT("Vector/RoadSplines.gpkg"));
		TestTrue(TEXT("the engine line names the file"), EngineLine.Contains(TEXT("Vector/RoadSplinesEngine.gpkg")));
		TestTrue(TEXT("and says it is the engine-datum copy"), EngineLine.Contains(TEXT("engine-datum copy")));
		TestEqual(TEXT("the shared layer's line is the one the importer has always written"),
			SharedLine, FString(TEXT("Road splines read from Vector/RoadSplines.gpkg.")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
