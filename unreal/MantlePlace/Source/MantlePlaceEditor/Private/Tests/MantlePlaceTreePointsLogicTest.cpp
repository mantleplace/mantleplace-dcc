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
		// point_count 0 == the manifest published none, so no cross-check runs here. The count
		// table itself is a shared vector (corpus case manifest.treePointsRowCount) rather than a
		// literal in this file — what stays here is the frame math, which is this host's own.
		TestTrue(TEXT("csv parses"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        Csv, OriginEastingM, OriginNorthingM, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
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

	// --- Columns by header name: an added column or a new order yields the same rows ---
	{
		// The manifest publishes the column NAMES, so the names are the contract and the order is
		// not. A reader keyed on position would put a height into a crown radius on a reorder, and
		// one keyed on the exact header would drop the whole layer on an added column.
		const FString Reference =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n")
		    TEXT("442059.50,4014272.50,,8.00,2.80\n");
		const FString ExtraColumn =
		    TEXT("x,y,ground_z,height_m,crown_radius_m,foliage_type\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34,tree\n")
		    TEXT("442059.50,4014272.50,,8.00,2.80,shrub\n");
		const FString Reordered =
		    TEXT(" Height_M , crown_radius_m,x,y,ground_z\n")
		        TEXT("12.40,4.34,441959.50,4014372.50,2640.96\n")
		    TEXT("8.00,2.80,442059.50,4014272.50,\n");

		TArray<FMantlePlaceTreePointRow> Expected;
		FString Error;
		FMantlePlaceTreePointsLogic::ParseCsv(
		    Reference, OriginEastingM, OriginNorthingM, /*DeclaredPointCount*/ 0, Expected, Error);
		TestEqual(TEXT("reference parses 2 rows"), Expected.Num(), 2);

		const TPair<const TCHAR*, const FString*> Variants[] = {
		    {TEXT("extra trailing column"), &ExtraColumn},
		    {TEXT("reordered, cased and padded header"), &Reordered},
		};
		for (const TPair<const TCHAR*, const FString*>& Variant : Variants)
		{
			TArray<FMantlePlaceTreePointRow> Rows;
			FString VariantError;
			TestTrue(FString::Printf(TEXT("%s parses"), Variant.Key),
			    FMantlePlaceTreePointsLogic::ParseCsv(*Variant.Value, OriginEastingM, OriginNorthingM,
			        /*DeclaredPointCount*/ 2, Rows, VariantError)
			        == EMantlePlaceTreePointsOutcome::Parsed);
			TestEqual(FString::Printf(TEXT("%s: no error"), Variant.Key), VariantError, FString());
			TestEqual(FString::Printf(TEXT("%s: row count"), Variant.Key), Rows.Num(), Expected.Num());
			for (int32 Index = 0; Index < FMath::Min(Rows.Num(), Expected.Num()); ++Index)
			{
				TestEqual(FString::Printf(TEXT("%s: row %d position"), Variant.Key, Index),
				    Rows[Index].Position, Expected[Index].Position, 1e-6f);
				TestEqual(FString::Printf(TEXT("%s: row %d height"), Variant.Key, Index),
				    Rows[Index].HeightM, Expected[Index].HeightM, 1e-6f);
				TestEqual(FString::Printf(TEXT("%s: row %d crown"), Variant.Key, Index),
				    Rows[Index].CrownRadiusM, Expected[Index].CrownRadiusM, 1e-6f);
				TestEqual(FString::Printf(TEXT("%s: row %d ground"), Variant.Key, Index),
				    Rows[Index].GroundZM, Expected[Index].GroundZM, 1e-6f);
			}
		}
	}

	// --- Fail-closed on a changed/missing header (ETL column-contract drift) ---
	{
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		// A drifted column contract is its OWN outcome, not the count mismatch: the importer skips
		// the layer here and fails the import there, so a reader that collapses the two either
		// reports a good bundle as failed or a truncated one as fine.
		TestTrue(TEXT("wrong header is HeaderUnrecognised"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        TEXT("lon,lat,z\n1,2,3\n"), OriginEastingM, OriginNorthingM,
		        /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestFalse(TEXT("and it says why"), Error.IsEmpty());
		TestTrue(TEXT("a missing required column is HeaderUnrecognised"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        TEXT("x,y,ground_z,crown_radius_m,foliage_type\n441959.50,4014372.50,2640.96,4.34,tree\n"),
		        OriginEastingM, OriginNorthingM, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestTrue(TEXT("and it names the missing column"), Error.Contains(TEXT("\"height_m\"")));
		TestTrue(TEXT("empty text is HeaderUnrecognised too"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        FString(), OriginEastingM, OriginNorthingM, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
