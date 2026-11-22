// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDrapeAlignmentLogic.h"
#include "MantlePlaceImportManifest.h"

namespace
{
FMantlePlaceVaultManifest MakeManifest(double DrapeNorthM, double DrapeEastM)
{
	FMantlePlaceVaultManifest M;
	M.Heightmap.bPresent = true;
	M.Heightmap.SectionSizeQuads = 63;
	M.Heightmap.SectionsPerComponent = 2;
	M.Heightmap.ComponentCountX = 4;
	M.Heightmap.ComponentCountY = 4;
	M.Heightmap.ScaleXPercent = 100000.0 / 504.0;
	M.Heightmap.ScaleYPercent = 100000.0 / 504.0;

	M.Drape.bPresent = true;
	M.Georef.OriginEastingM = 500000.0;
	M.Georef.OriginNorthingM = 4000000.0;
	M.Drape.LeftM = M.Georef.OriginEastingM - DrapeEastM / 2.0;
	M.Drape.RightM = M.Georef.OriginEastingM + DrapeEastM / 2.0;
	M.Drape.BottomM = M.Georef.OriginNorthingM - DrapeNorthM / 2.0;
	M.Drape.TopM = M.Georef.OriginNorthingM + DrapeNorthM / 2.0;
	return M;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceDrapeAlignmentLogicTest,
    "MantlePlace.Import.DrapeAlignmentLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceDrapeAlignmentLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceDrapeAlignmentLogic;

	{
		const FMantlePlaceDrapeAlignment Alignment = FLogic::Classify(FString());
		TestTrue(TEXT("no descriptor -> Absent"), Alignment.Shape == EMantlePlaceDrapeAlignment::Absent);
		TestFalse(TEXT("absent never warns"), Alignment.ShouldWarn());
		TestEqual(TEXT("absent logs nothing"), FLogic::DescribeForLog(Alignment), FString());
	}

	{
		TestTrue(TEXT("the exact aligned string"),
			FLogic::Classify(TEXT("matches heightmap AOI-UTM extent")).Shape
				== EMantlePlaceDrapeAlignment::Matches);
		TestTrue(TEXT("the aligned shape with more said after it"),
			FLogic::Classify(TEXT("matches heightmap AOI-UTM extent (to within 1 px)")).Shape
				== EMantlePlaceDrapeAlignment::Matches);
		TestTrue(TEXT("surrounding whitespace is not a different shape"),
			FLogic::Classify(TEXT("  matches heightmap AOI-UTM extent  ")).Shape
				== EMantlePlaceDrapeAlignment::Matches);
		TestTrue(TEXT("the unverified shape, whatever dash it is punctuated with"),
			FLogic::Classify(TEXT("unverified - heightmap extent unavailable")).Shape
				== EMantlePlaceDrapeAlignment::Unverified);
		TestTrue(TEXT("a shape from a newer producer is echoed, not refused"),
			FLogic::Classify(TEXT("resampled onto the heightmap grid")).Shape
				== EMantlePlaceDrapeAlignment::Unrecognised);
	}

	{
		const FMantlePlaceDrapeAlignment Alignment = FLogic::Classify(
			TEXT("diverges from heightmap extent (imagery covers 68.9% of the heightmap, overshoot 1.12x)"));
		TestTrue(TEXT("divergent shape"), Alignment.Shape == EMantlePlaceDrapeAlignment::Diverges);
		TestTrue(TEXT("divergent shape warns"), Alignment.ShouldWarn());
		TestTrue(TEXT("both figures were read"), Alignment.bHasMeasured);
		TestEqual(TEXT("covered percent"), Alignment.CoveredPercent, 68.9, 1e-9);
		TestEqual(TEXT("overshoot ratio"), Alignment.OvershootRatio, 1.12, 1e-9);

		const FMantlePlaceDrapeAlignment Reworded = FLogic::Classify(
			TEXT("diverges from heightmap extent (overshoot 1.12x; imagery covers 68.9% of the heightmap)"));
		TestEqual(TEXT("reworded: covered percent still 68.9"), Reworded.CoveredPercent, 68.9, 1e-9);
		TestEqual(TEXT("reworded: overshoot still 1.12"), Reworded.OvershootRatio, 1.12, 1e-9);

		const FMantlePlaceDrapeAlignment Partial =
			FLogic::Classify(TEXT("diverges from heightmap extent (imagery covers 68.9% of the heightmap)"));
		TestTrue(TEXT("a half-readable sentence is still the divergent shape"),
			Partial.Shape == EMantlePlaceDrapeAlignment::Diverges);
		TestFalse(TEXT("and reports no measurements at all"), Partial.bHasMeasured);
		TestTrue(TEXT("its log line still carries the manifest's own words"),
			FLogic::DescribeForLog(Partial).Contains(TEXT("imagery covers 68.9%")));
	}

	{
		const FString Warned = FLogic::DescribeForLog(FLogic::Classify(
			TEXT("diverges from heightmap extent (imagery covers 68.9% of the heightmap, overshoot 1.12x)")));
		TestTrue(TEXT("the divergent shape is a WARNING"), Warned.StartsWith(TEXT("WARNING")));
		TestTrue(TEXT("and carries the measured coverage"), Warned.Contains(TEXT("68.9")));
		TestTrue(TEXT("and the measured overshoot"), Warned.Contains(TEXT("1.12")));

		const FString Noted =
			FLogic::DescribeForLog(FLogic::Classify(TEXT("unverified - heightmap extent unavailable")));
		TestFalse(TEXT("the unverified shape is not a warning"), Noted.StartsWith(TEXT("WARNING")));
		TestFalse(TEXT("but it is still said out loud"), Noted.IsEmpty());
	}

	{
		FString Error;

		const FMantlePlaceDrapeAlignment Matches = FLogic::Classify(TEXT("matches heightmap AOI-UTM extent"));

		Error.Reset();
		TestTrue(TEXT("an exactly-matching drape passes"),
			FLogic::CheckAgainstExtents(MakeManifest(1000.0, 1000.0), Matches, Error));

		Error.Reset();
		TestTrue(TEXT("half a percent short still counts as matching"),
			FLogic::CheckAgainstExtents(MakeManifest(995.0, 1000.0), Matches, Error));

		Error.Reset();
		TestFalse(TEXT("five percent short contradicts the claim"),
			FLogic::CheckAgainstExtents(MakeManifest(950.0, 1000.0), Matches, Error));
		TestTrue(TEXT("and the refusal quotes what the manifest claimed"),
			Error.Contains(TEXT("matches heightmap AOI-UTM extent")));

		Error.Reset();
		TestFalse(TEXT("five percent long contradicts it as well"),
			FLogic::CheckAgainstExtents(MakeManifest(1000.0, 1050.0), Matches, Error));

		Error.Reset();
		TestTrue(TEXT("a divergent claim on a divergent drape proceeds"),
			FLogic::CheckAgainstExtents(
				MakeManifest(600.0, 600.0),
				FLogic::Classify(TEXT("diverges from heightmap extent (imagery covers 36.0% of the "
				                      "heightmap, overshoot 1.00x)")),
				Error));
		Error.Reset();
		TestTrue(TEXT("an unrecognised shape proceeds"),
			FLogic::CheckAgainstExtents(
				MakeManifest(600.0, 600.0), FLogic::Classify(TEXT("something new")), Error));

		FMantlePlaceVaultManifest NoHeightmap = MakeManifest(600.0, 600.0);
		NoHeightmap.Heightmap.bPresent = false;
		Error.Reset();
		TestTrue(TEXT("no heightmap, nothing to contradict"),
			FLogic::CheckAgainstExtents(NoHeightmap, Matches, Error));

		FMantlePlaceVaultManifest NoDrape = MakeManifest(600.0, 600.0);
		NoDrape.Drape.bPresent = false;
		Error.Reset();
		TestTrue(TEXT("no drape, nothing to contradict"),
			FLogic::CheckAgainstExtents(NoDrape, Matches, Error));
	}

	return true;
}

#endif
