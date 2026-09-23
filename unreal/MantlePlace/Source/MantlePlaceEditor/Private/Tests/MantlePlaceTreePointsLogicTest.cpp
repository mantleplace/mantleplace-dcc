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
	// The reference fixture's landscape, in UE axis order (X = North, Y = East): 8 x 126 quads at
	// 138.988... cm north and 141.369... cm east. What FMantlePlaceVaultManifest::GetAoiSizeUeCm()
	// returns for it; the corpus case manifest.treePointsFrame drives the two together.
	const FVector2D LandscapeSpanUeCm(140100.0, 142500.0);
	// A pointer that states no frame: every bundle older than MPB 1.3.0, and the case the extent
	// substitute is the whole of the check for. The stated frame has its own block below.
	const FMantlePlaceTreePointsFrame Unstated;

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
		        Csv, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
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
		    Reference, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Expected, Error);
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
			    FMantlePlaceTreePointsLogic::ParseCsv(*Variant.Value, OriginEastingM, OriginNorthingM, Unstated,
			        LandscapeSpanUeCm, /*DeclaredPointCount*/ 2, Rows, VariantError)
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
		        TEXT("lon,lat,z\n1,2,3\n"), OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm,
		        /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestFalse(TEXT("and it says why"), Error.IsEmpty());
		TestTrue(TEXT("a missing required column is HeaderUnrecognised"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        TEXT("x,y,ground_z,crown_radius_m,foliage_type\n441959.50,4014372.50,2640.96,4.34,tree\n"),
		        OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestTrue(TEXT("and it names the missing column"), Error.Contains(TEXT("\"height_m\"")));
		TestTrue(TEXT("empty text is HeaderUnrecognised too"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        FString(), OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
	}

	// --- A file that is not in this host's frame is refused, never converted (HPS-53) ---
	{
		// What a State Plane foot delivery publishes beside the same metric UTM origin. Subtracting
		// the origin from these succeeds, which is the whole hazard: the result is a finite number
		// more than a thousand kilometres from the site, and it looks exactly like a position.
		const FString FootFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("1817612.40,1919384.75,8664.57,40.68,14.24\n")
		    TEXT("1817940.49,1919056.66,,26.25,9.19\n");

		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("a foot-frame file is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 2, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and it keeps none of the rows"), Rows.Num(), 0);
		TestTrue(TEXT("and it names the evidence"), Error.Contains(TEXT("outside the landscape extent")));
		TestTrue(TEXT("and how much of the file it was"), Error.Contains(TEXT("2 of 2")));

		// The frame belongs to the file, not to the point: one row off the landscape refuses the
		// layer rather than being dropped from it. Keeping the rows that happen to land inside
		// would be placing a file there is evidence against.
		const FString OneOutside =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n")
		    TEXT("451959.50,4014372.50,2640.96,8.00,2.80\n"); // 10 km east of a 1.4 km landscape
		TestTrue(TEXT("one point outside refuses the whole file"),
		    FMantlePlaceTreePointsLogic::ParseCsv(OneOutside, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and the rows inside go with it"), Rows.Num(), 0);
		TestTrue(TEXT("and the count says it was one"), Error.Contains(TEXT("1 of 2")));

		// The refusal outranks the count: whether every row survived the parse is a question about
		// a file this host places, and this one it does not.
		TestTrue(TEXT("a foot-frame file with a wrong count is still Unplaceable, not CountMismatch"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 5, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);

		// No published extent is no evidence either way, and an unstated frame is never assumed to
		// match: with nothing to compare against, the file is unplaceable rather than trusted.
		const FString InFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n441959.50,4014372.50,2640.96,12.40,4.34\n");
		TestTrue(TEXT("no landscape extent to check against is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Unstated,
		        FVector2D::ZeroVector, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and it keeps no rows"), Rows.Num(), 0);
		TestTrue(TEXT("and it says that is why"), Error.Contains(TEXT("no landscape extent")));
	}

	// --- A stated frame is read, and the extent stays as the backstop (HPS-53, MPB 1.3.0) ---
	// The wrong-CRS, wrong-unit and half-stated refusals are shared vectors (corpus case
	// manifest.treePointsStatedFrame), driven through the parser. What stays here is what a
	// manifest cannot reach on its own: the two halves meeting.
	{
		FMantlePlaceTreePointsFrame Stated;
		Stated.Crs = TEXT("EPSG:32613");
		Stated.Units = TEXT("m");
		Stated.HorizontalUnits = TEXT("m");
		Stated.HostCrs = TEXT("EPSG:32613");
		Stated.bRequired = true;

		const FString InFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n441959.50,4014372.50,2640.96,12.40,4.34\n");
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("a stated host frame inside the extent is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Stated,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 1, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and keeps its row"), Rows.Num(), 1);

		// A producer can state a frame wrongly, so the backstop still refuses on evidence: foot
		// coordinates under a pointer that says metres are off the landscape all the same.
		const FString FootFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n1817612.40,1919384.75,8664.57,40.68,14.24\n");
		TestTrue(TEXT("a stated host frame the rows contradict is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Stated,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestTrue(TEXT("on the extent's evidence"), Error.Contains(TEXT("outside the landscape extent")));

		// No extent is no evidence either way. Behind a stated frame that matched, that is not a
		// reason to refuse — a bundle with a terrain mesh and no heightmap brings its trees.
		TestTrue(TEXT("a stated host frame with no landscape extent is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Stated,
		        FVector2D::ZeroVector, /*DeclaredPointCount*/ 1, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and keeps its row"), Rows.Num(), 1);

		// Compared verbatim: deciding that two spellings name one CRS is a derivation, and a host
		// that does not make it refuses rather than guesses. The producer writes the pointer's CRS
		// from the georeference's own string, so the two agree to the byte when the frame is ours.
		FMantlePlaceTreePointsFrame Respelled = Stated;
		Respelled.Crs = TEXT("epsg:32613");
		TestTrue(TEXT("a CRS spelled otherwise than the georeference's is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Respelled,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and keeps no rows"), Rows.Num(), 0);

		// A georeference with no crs_projected leaves nothing to show the stated frame is ours.
		FMantlePlaceTreePointsFrame NoHost = Stated;
		NoHost.HostCrs.Reset();
		TestTrue(TEXT("a stated frame with no host CRS to match is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, NoHost,
		        LandscapeSpanUeCm, /*DeclaredPointCount*/ 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestTrue(TEXT("and names the CRS it was given"), Error.Contains(TEXT("\"EPSG:32613\"")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
