// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceTreePointsLogic.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceTreePointsLogicTest,
    "MantlePlace.Import.TreePointsLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTreePointsLogicTest::RunTest(const FString& Parameters)
{
	const double OriginEastingM = 441959.5;
	const double OriginNorthingM = 4014372.5;

	// --- Happy path: rows -> Local Projected Frame, empty ground_z tolerated, bad row skipped ---
	{
		const FString Csv =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n") // at the origin
		    TEXT("442059.50,4014272.50,,8.00,2.80\n")             // 100 m east, 100 m south, no DEM data
		    TEXT("not,a,valid,row\n")                             // malformed -> skipped
		    TEXT("441859.50,4014472.50,2650.00,3.10,1.09\n");     // 100 m west, 100 m north

		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("csv parses"), FMantlePlaceTreePointsLogic::ParseCsv(
		                                 Csv, OriginEastingM, OriginNorthingM, Rows, Error));
		TestEqual(TEXT("no parse error"), Error, FString());
		TestEqual(TEXT("3 valid rows (malformed skipped)"), Rows.Num(), 3);

		if (Rows.Num() == 3)
		{
			TestEqual(TEXT("origin tree at world X=0"), Rows[0].Position.X, 0.0, 1e-6);
			TestEqual(TEXT("origin tree at world Y=0"), Rows[0].Position.Y, 0.0, 1e-6);
			TestEqual(TEXT("ground_z -> Z cm"), Rows[0].Position.Z, 264096.0, 1e-3);
			TestEqual(TEXT("height carried"), Rows[0].HeightM, 12.4f, 1e-4f);
			TestEqual(TEXT("crown carried"), Rows[0].CrownRadiusM, 4.34f, 1e-4f);

			// UE is left-handed: North -> +X, East -> +Y. These rows are pure UTM offsets, so both
			// components are exact and a swapped axis cannot hide behind a tolerance.
			TestEqual(TEXT("south 100 m -> -10000 cm X"), Rows[1].Position.X, -10000.0, 1e-6);
			TestEqual(TEXT("east 100 m -> +10000 cm Y"), Rows[1].Position.Y, 10000.0, 1e-6);
			TestEqual(TEXT("empty ground_z -> Z 0"), Rows[1].Position.Z, 0.0, 1e-9);

			TestEqual(TEXT("north 100 m -> +10000 cm X"), Rows[2].Position.X, 10000.0, 1e-6);
			TestEqual(TEXT("west 100 m -> -10000 cm Y"), Rows[2].Position.Y, -10000.0, 1e-6);
		}
	}

	// --- Fail-closed on a changed/missing header (ETL column-contract drift) ---
	{
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestFalse(TEXT("wrong header fails"), FMantlePlaceTreePointsLogic::ParseCsv(
		                                          TEXT("lon,lat,z\n1,2,3\n"), OriginEastingM, OriginNorthingM, Rows, Error));
		TestFalse(TEXT("empty text fails"), FMantlePlaceTreePointsLogic::ParseCsv(
		                                        FString(), OriginEastingM, OriginNorthingM, Rows, Error));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
