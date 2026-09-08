// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDrapeAlignmentLogic.h"
#include "MantlePlaceImportManifest.h"

// The three shapes themselves, and the refusal, are pinned by the shared conformance corpus
// (manifest.drapeAlignment*, manifest.reject.drapeAlignmentContradiction) and driven by
// MantlePlace.Import.Manifest. What is asserted HERE is the behaviour of this host's helper around
// those fixtures: that the prefix match is a PREFIX match rather than an equality test, that the
// measured figures are read by anchor rather than by position, and where the extent slack ends.
// Those are the properties a fixture cannot state, because each of them is about the inputs the
// corpus deliberately does not enumerate.

namespace
{
/** A manifest whose AOI is 1000 m north x 1000 m east, with a drape of the given span centred on
 *  it. Only the fields the alignment check reads are filled: it looks at two rectangles. */
FMantlePlaceVaultManifest MakeManifest(double DrapeNorthM, double DrapeEastM)
{
	FMantlePlaceVaultManifest M;
	M.bHasHeightmap = true;
	M.SectionSizeQuads = 63;
	M.SectionsPerComponent = 2;
	M.ComponentCountX = 4;
	M.ComponentCountY = 4;
	// 4 components x 126 quads = 504 quads per axis; 1000 m / 504 quads = 198.412... cm per quad.
	M.ScaleXPercent = 100000.0 / 504.0; // easting axis (manifest naming) -> UE Y
	M.ScaleYPercent = 100000.0 / 504.0; // northing axis -> UE X

	M.bHasDrape = true;
	M.OriginEastingM = 500000.0;
	M.OriginNorthingM = 4000000.0;
	M.DrapeLeftM = M.OriginEastingM - DrapeEastM / 2.0;
	M.DrapeRightM = M.OriginEastingM + DrapeEastM / 2.0;
	M.DrapeBottomM = M.OriginNorthingM - DrapeNorthM / 2.0;
	M.DrapeTopM = M.OriginNorthingM + DrapeNorthM / 2.0;
	return M;
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceDrapeAlignmentLogicTest,
    "MantlePlace.Import.DrapeAlignmentLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceDrapeAlignmentLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceDrapeAlignmentLogic;

	// --- Absent is absent, and is not a defect ------------------------------------------------
	{
		const FMantlePlaceDrapeAlignment Alignment = FLogic::Classify(FString());
		TestTrue(TEXT("no descriptor -> Absent"), Alignment.Shape == EMantlePlaceDrapeAlignment::Absent);
		TestFalse(TEXT("absent never warns"), Alignment.ShouldWarn());
		TestEqual(TEXT("absent logs nothing"), FLogic::DescribeForLog(Alignment), FString());
	}

	// --- The match is on the PREFIX, which is what the schema instructs ------------------------
	// The ETL is free to lengthen any of these sentences in a minor release; a reader comparing
	// whole strings would silently fall through to Unrecognised the day it did.
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

	// --- The divergent shape's figures are read by ANCHOR, not by position ---------------------
	{
		const FMantlePlaceDrapeAlignment Alignment = FLogic::Classify(
			TEXT("diverges from heightmap extent (imagery covers 68.9% of the heightmap, overshoot 1.12x)"));
		TestTrue(TEXT("divergent shape"), Alignment.Shape == EMantlePlaceDrapeAlignment::Diverges);
		TestTrue(TEXT("divergent shape warns"), Alignment.ShouldWarn());
		TestTrue(TEXT("both figures were read"), Alignment.bHasMeasured);
		TestEqual(TEXT("covered percent"), Alignment.CoveredPercent, 68.9, 1e-9);
		TestEqual(TEXT("overshoot ratio"), Alignment.OvershootRatio, 1.12, 1e-9);

		// Reworded, with the two figures in the other order. Positional reads swap here; anchored
		// ones do not, and the whole reason to anchor is that this sentence is prose.
		const FMantlePlaceDrapeAlignment Reworded = FLogic::Classify(
			TEXT("diverges from heightmap extent (overshoot 1.12x; imagery covers 68.9% of the heightmap)"));
		TestEqual(TEXT("reworded: covered percent still 68.9"), Reworded.CoveredPercent, 68.9, 1e-9);
		TestEqual(TEXT("reworded: overshoot still 1.12"), Reworded.OvershootRatio, 1.12, 1e-9);

		// Both or neither. A warning quoting one measurement and inventing the other is worse than
		// one that quotes the manifest's own sentence, which is what this falls back to.
		const FMantlePlaceDrapeAlignment Partial =
			FLogic::Classify(TEXT("diverges from heightmap extent (imagery covers 68.9% of the heightmap)"));
		TestTrue(TEXT("a half-readable sentence is still the divergent shape"),
			Partial.Shape == EMantlePlaceDrapeAlignment::Diverges);
		TestFalse(TEXT("and reports no measurements at all"), Partial.bHasMeasured);
		TestTrue(TEXT("its log line still carries the manifest's own words"),
			FLogic::DescribeForLog(Partial).Contains(TEXT("imagery covers 68.9%")));
	}

	// --- The log line a user actually reads ----------------------------------------------------
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

	// --- Where the extent slack ends -----------------------------------------------------------
	// Only a "matches" claim is a claim about these two rectangles, so only it can be refuted.
	{
		FString Error;

		const FMantlePlaceDrapeAlignment Matches = FLogic::Classify(TEXT("matches heightmap AOI-UTM extent"));

		Error.Reset();
		TestTrue(TEXT("an exactly-matching drape passes"),
			FLogic::CheckAgainstExtents(MakeManifest(1000.0, 1000.0), Matches, Error));

		// 0.5% short on one axis: inside the slack, and the shape the reference bundle is in.
		Error.Reset();
		TestTrue(TEXT("half a percent short still counts as matching"),
			FLogic::CheckAgainstExtents(MakeManifest(995.0, 1000.0), Matches, Error));

		// 5% short: outside it, and the bundle says nothing about it. This is the silent misdrape.
		Error.Reset();
		TestFalse(TEXT("five percent short contradicts the claim"),
			FLogic::CheckAgainstExtents(MakeManifest(950.0, 1000.0), Matches, Error));
		TestTrue(TEXT("and the refusal quotes what the manifest claimed"),
			Error.Contains(TEXT("matches heightmap AOI-UTM extent")));

		// An oversized drape is a contradiction too — "matches" is not "covers at least".
		Error.Reset();
		TestFalse(TEXT("five percent long contradicts it as well"),
			FLogic::CheckAgainstExtents(MakeManifest(1000.0, 1050.0), Matches, Error));

		// Every other shape is believed. A divergent claim is what the ETL measured; the UV
		// transform already rides the declared extents, so there is nothing to fail closed over.
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

		// Nothing to compare against is not a contradiction: a mesh-only bundle with a drape is a
		// legitimate import, and so is a bundle whose transform has no components.
		FMantlePlaceVaultManifest NoHeightmap = MakeManifest(600.0, 600.0);
		NoHeightmap.bHasHeightmap = false;
		Error.Reset();
		TestTrue(TEXT("no heightmap, nothing to contradict"),
			FLogic::CheckAgainstExtents(NoHeightmap, Matches, Error));

		FMantlePlaceVaultManifest NoDrape = MakeManifest(600.0, 600.0);
		NoDrape.bHasDrape = false;
		Error.Reset();
		TestTrue(TEXT("no drape, nothing to contradict"),
			FLogic::CheckAgainstExtents(NoDrape, Matches, Error));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
