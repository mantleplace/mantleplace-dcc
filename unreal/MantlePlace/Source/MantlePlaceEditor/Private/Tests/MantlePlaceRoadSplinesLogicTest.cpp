// Copyright Mantle Place. All Rights Reserved.

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
// structure and no other host produces it (DOC-06).

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
		FString Error;
		TestTrue(TEXT("geojson parses"),
			FLogic::ParseGeoJson(GeoJson, OriginEastingM, OriginNorthingM, Epsg, Splines, Error));
		TestEqual(TEXT("no parse error"), Error, FString());
		TestEqual(TEXT("LineString + 2 MultiLineString parts, Point skipped"), Splines.Num(), 3);

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
		FString Error;
		TestFalse(TEXT("invalid JSON fails"),
			FLogic::ParseGeoJson(TEXT("not json"), OriginEastingM, OriginNorthingM, Epsg, Splines, Error));
		TestFalse(TEXT("missing features fails"),
			FLogic::ParseGeoJson(TEXT("{\"type\":\"FeatureCollection\"}"),
				OriginEastingM, OriginNorthingM, Epsg, Splines, Error));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
