// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

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
	const FVector2D LandscapeSpanUeCm(140100.0, 142500.0);
	const FMantlePlaceTreePointsFrame Unstated;

	{
		const FString Csv =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n")
		    TEXT("442059.50,4014272.50,,8.00,2.80\n")
		    TEXT("not,a,valid,row\n")
		    TEXT("441859.50,4014472.50,2650.00,3.10,1.09\n");

		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("csv parses"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        Csv, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 0, Rows, Error)
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

			TestEqual(TEXT("south 100 m -> -10000 cm X"), Rows[1].Position.X, -10000.0, 1e-6);
			TestEqual(TEXT("east 100 m -> +10000 cm Y"), Rows[1].Position.Y, 10000.0, 1e-6);
			TestEqual(TEXT("empty ground_z -> Z 0"), Rows[1].Position.Z, 0.0, 1e-9);

			TestEqual(TEXT("north 100 m -> +10000 cm X"), Rows[2].Position.X, 10000.0, 1e-6);
			TestEqual(TEXT("west 100 m -> -10000 cm Y"), Rows[2].Position.Y, -10000.0, 1e-6);
		}
	}

	{
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
		    Reference, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 0, Expected, Error);
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
			        LandscapeSpanUeCm, 2, Rows, VariantError)
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

	{
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("wrong header is HeaderUnrecognised"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        TEXT("lon,lat,z\n1,2,3\n"), OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm,
		        0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestFalse(TEXT("and it says why"), Error.IsEmpty());
		TestTrue(TEXT("a missing required column is HeaderUnrecognised"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        TEXT("x,y,ground_z,crown_radius_m,foliage_type\n441959.50,4014372.50,2640.96,4.34,tree\n"),
		        OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
		TestTrue(TEXT("and it names the missing column"), Error.Contains(TEXT("\"height_m\"")));
		TestTrue(TEXT("empty text is HeaderUnrecognised too"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        FString(), OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::HeaderUnrecognised);
	}

	{
		const FString FootFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("1817612.40,1919384.75,8664.57,40.68,14.24\n")
		    TEXT("1817940.49,1919056.66,,26.25,9.19\n");

		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("a foot-frame file is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 2, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and it keeps none of the rows"), Rows.Num(), 0);
		TestTrue(TEXT("and it names the evidence"), Error.Contains(TEXT("outside the landscape extent")));
		TestTrue(TEXT("and how much of the file it was"), Error.Contains(TEXT("2 of 2")));

		const FString OneOutside =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n")
		    TEXT("451959.50,4014372.50,2640.96,8.00,2.80\n");
		TestTrue(TEXT("one point outside refuses the whole file"),
		    FMantlePlaceTreePointsLogic::ParseCsv(OneOutside, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and the rows inside go with it"), Rows.Num(), 0);
		TestTrue(TEXT("and the count says it was one"), Error.Contains(TEXT("1 of 2")));

		TestTrue(TEXT("a foot-frame file with a wrong count is still Unplaceable, not CountMismatch"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 5, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);

		const FString InFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n441959.50,4014372.50,2640.96,12.40,4.34\n");
		TestTrue(TEXT("no landscape extent to check against is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Unstated,
		        FVector2D::ZeroVector, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and it keeps no rows"), Rows.Num(), 0);
		TestTrue(TEXT("and it says that is why"), Error.Contains(TEXT("no landscape extent")));
	}

	{
		const FString Header = TEXT("x,y,ground_z,height_m,crown_radius_m\n");
		auto Row = [&](double EastingM, double NorthingM)
		{
			return FString::Printf(TEXT("%.2f,%.2f,2640.96,12.40,4.34\n"), OriginEastingM + EastingM,
			    OriginNorthingM + NorthingM);
		};
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error, Note;

		const FString OnEdge = Header + Row(712.5, 0.0) + Row(-712.5, 0.0) + Row(0.0, 700.5) + Row(0.0, -700.5);
		TestTrue(TEXT("points exactly on the extent's edge are Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(OnEdge, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 4, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and all four are kept"), Rows.Num(), 4);
		TestTrue(TEXT("with nothing left out to name"), Note.IsEmpty());

		TestTrue(TEXT("a point 1 cm past the edge is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(Header + Row(712.51, 0.0) + Row(-712.51, 0.0),
		        OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 2, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and both are kept"), Rows.Num(), 2);

		FString Twenty = Header + Row(713.0, 0.0);
		for (int32 Index = 0; Index < 19; ++Index)
		{
			Twenty += Row(Index, -Index);
		}
		TestTrue(TEXT("one point just outside 19 inside is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(Twenty, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 20, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and places the 19 inside"), Rows.Num(), 19);
		TestTrue(TEXT("and names the count left out"), Note.Contains(TEXT("1 of 20")));
		TestTrue(TEXT("and the first one"), Note.Contains(FString::Printf(TEXT("x=%.2f"), OriginEastingM + 713.0)));
		TestTrue(TEXT("and how many were placed"), Note.Contains(TEXT("The other 19")));
		TestEqual(TEXT("with no error"), Error, FString());

		TestTrue(TEXT("a declared count that disagrees with the points read is still CountMismatch"),
		    FMantlePlaceTreePointsLogic::ParseCsv(Twenty, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 19, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::CountMismatch);
		TestTrue(TEXT("and leaves no note"), Note.IsEmpty());

		FString Nineteen = Header + Row(713.0, 0.0);
		for (int32 Index = 0; Index < 18; ++Index)
		{
			Nineteen += Row(Index, -Index);
		}
		TestTrue(TEXT("one point outside 18 inside is over the share and Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(Nineteen, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 0, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and keeps no rows"), Rows.Num(), 0);
		TestTrue(TEXT("and it says 1 of 19"), Error.Contains(TEXT("1 of 19")));
		TestTrue(TEXT("and leaves no note"), Note.IsEmpty());

		FString Live = Header;
		for (int32 Index = 0; Index < 3531; ++Index)
		{
			Live += Row((Index % 700) - 350, (Index % 600) - 300);
		}
		for (int32 Index = 0; Index < 20; ++Index)
		{
			Live += Row(-712.5 - (Index % 2) * 0.5, Index);
		}
		TestTrue(TEXT("20 of 3551 on or just over the edge is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(Live, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 3551, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("with the edge points kept and the ones over it left out"), Rows.Num(), 3531 + 10);
		TestTrue(TEXT("naming 10 of 3551"), Note.Contains(TEXT("10 of 3551")));

		const FString FootFrame =
		    Header + TEXT("1817612.40,1919384.75,8664.57,40.68,14.24\n1817940.49,1919056.66,,26.25,9.19\n");
		TestTrue(TEXT("a foot-frame file is still Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Unstated,
		        LandscapeSpanUeCm, 2, Rows, Error, FString(), &Note)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
	}

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
		        LandscapeSpanUeCm, 1, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and keeps its row"), Rows.Num(), 1);

		const FString FootFrame =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n1817612.40,1919384.75,8664.57,40.68,14.24\n");
		TestTrue(TEXT("a stated host frame the rows contradict is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(FootFrame, OriginEastingM, OriginNorthingM, Stated,
		        LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestTrue(TEXT("on the extent's evidence"), Error.Contains(TEXT("outside the landscape extent")));

		TestTrue(TEXT("a stated host frame with no landscape extent is Parsed"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Stated,
		        FVector2D::ZeroVector, 1, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		TestEqual(TEXT("and keeps its row"), Rows.Num(), 1);

		FMantlePlaceTreePointsFrame Respelled = Stated;
		Respelled.Crs = TEXT("epsg:32613");
		TestTrue(TEXT("a CRS spelled otherwise than the georeference's is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, Respelled,
		        LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestEqual(TEXT("and keeps no rows"), Rows.Num(), 0);

		FMantlePlaceTreePointsFrame NoHost = Stated;
		NoHost.HostCrs.Reset();
		TestTrue(TEXT("a stated frame with no host CRS to match is Unplaceable"),
		    FMantlePlaceTreePointsLogic::ParseCsv(InFrame, OriginEastingM, OriginNorthingM, NoHost,
		        LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Unplaceable);
		TestTrue(TEXT("and names the CRS it was given"), Error.Contains(TEXT("\"EPSG:32613\"")));
	}

	{
		const FString Csv =
		    TEXT("x,y,ground_z,height_m,crown_radius_m\n")
		        TEXT("441959.50,4014372.50,2640.96,12.40,4.34\n")
		    TEXT("442059.50,4014272.50,,8.00,2.80\n");
		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("csv parses"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        Csv, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 0, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		if (TestEqual(TEXT("two rows"), Rows.Num(), 2))
		{
			TestEqual(TEXT("height in metres is as published"), Rows[0].HeightM, 12.4f, 1e-4f);
			TestEqual(TEXT("height in centimetres is the same height in the engine's unit"), Rows[0].HeightCm, 1240.0f, 1e-2f);
			TestEqual(TEXT("crown radius in centimetres"), Rows[0].CrownRadiusCm, 434.0f, 1e-2f);
			TestTrue(TEXT("a measured ground height is known"), Rows[0].bGroundKnown);
			TestFalse(TEXT("an empty ground_z is not a measured ground height"), Rows[1].bGroundKnown);
			TestEqual(TEXT("and its Z is the documented 0"), Rows[1].Position.Z, 0.0, 1e-9);
			TestTrue(TEXT("no vocabulary, no column: every row is a tree"),
			    Rows[0].FoliageType == EMantlePlaceFoliageType::Tree && Rows[1].FoliageType == EMantlePlaceFoliageType::Tree);
		}
	}

	{
		const FString Csv =
		    TEXT("x,y,ground_z,height_m,crown_radius_m,foliage_type\n")
		        TEXT("441959.50,4014372.50,2640.96,4.10,1.40,shrub\n")
		    TEXT("442059.50,4014272.50,2638.00,3.20,1.05,tree\n");

		TArray<FMantlePlaceTreePointRow> Rows;
		FString Error;
		TestTrue(TEXT("csv parses with a vocabulary"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        Csv, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 2, Rows, Error, TEXT("1"))
		        == EMantlePlaceTreePointsOutcome::Parsed);
		if (TestEqual(TEXT("both rows kept"), Rows.Num(), 2))
		{
			TestTrue(TEXT("a shrub is a shrub"), Rows[0].FoliageType == EMantlePlaceFoliageType::Shrub);
			TestTrue(TEXT("a tree is a tree"), Rows[1].FoliageType == EMantlePlaceFoliageType::Tree);
		}

		TestTrue(TEXT("csv parses without a vocabulary"),
		    FMantlePlaceTreePointsLogic::ParseCsv(
		        Csv, OriginEastingM, OriginNorthingM, Unstated, LandscapeSpanUeCm, 2, Rows, Error)
		        == EMantlePlaceTreePointsOutcome::Parsed);
		if (TestEqual(TEXT("both rows kept without a vocabulary"), Rows.Num(), 2))
		{
			TestTrue(TEXT("without a vocabulary the column is not read"),
			    Rows[0].FoliageType == EMantlePlaceFoliageType::Tree);
		}

		TestTrue(TEXT("shrub"), FMantlePlaceTreePointsLogic::ReadFoliageType(TEXT("shrub")) == EMantlePlaceFoliageType::Shrub);
		TestTrue(TEXT("CSV padding is not part of the value"),
		    FMantlePlaceTreePointsLogic::ReadFoliageType(TEXT(" shrub ")) == EMantlePlaceFoliageType::Shrub);
		TestTrue(TEXT("Shrub is not shrub"), FMantlePlaceTreePointsLogic::ReadFoliageType(TEXT("Shrub")) == EMantlePlaceFoliageType::Tree);
		TestTrue(TEXT("shrubland is not shrub"), FMantlePlaceTreePointsLogic::ReadFoliageType(TEXT("shrubland")) == EMantlePlaceFoliageType::Tree);
		TestTrue(TEXT("an unknown value is a tree"), FMantlePlaceTreePointsLogic::ReadFoliageType(TEXT("hedgerow")) == EMantlePlaceFoliageType::Tree);
		TestTrue(TEXT("an empty cell is a tree"), FMantlePlaceTreePointsLogic::ReadFoliageType(FString()) == EMantlePlaceFoliageType::Tree);
	}

	{
		TArray<FName> Actual;
		for (TFieldIterator<FProperty> It(FMantlePlaceTreePointRow::StaticStruct()); It; ++It)
		{
			Actual.Add(It->GetFName());
			TestTrue(FString::Printf(TEXT("column %s is readable from a Blueprint"), *It->GetName()),
			    It->HasAnyPropertyFlags(CPF_BlueprintVisible));
		}
		const auto Join = [](const TArray<FName>& Names) {
			TArray<FString> Strings;
			for (const FName& Name : Names)
			{
				Strings.Add(Name.ToString());
			}
			return FString::Join(Strings, TEXT(", "));
		};
		TestEqual(TEXT("the contract lists exactly the struct's columns, in order"),
		    Join(FMantlePlaceTreePointsLogic::ColumnContract()), Join(Actual));
	}

	return true;
}

#endif
