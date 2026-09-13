// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportTiming.h"

// Timing exists to be compared between two runs, so the properties worth asserting are the ones a
// comparison depends on: every phase produces exactly one row, an import produces exactly one
// block, the total is wall time rather than a sum of the parts, and a call from outside an import
// does nothing at all.
//
// `BuildSummary()` returns lines rather than logging them precisely so this can assert the block's
// shape headlessly. A test that grepped UE_LOG output would be testing the log system.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportTimingTest,
    "MantlePlace.Import.Timing",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
/** Count the summary rows that carry a given phase label. */
int32 RowsMentioning(const TArray<FString>& Lines, const TCHAR* Needle)
{
	int32 Count = 0;
	for (const FString& Line : Lines)
	{
		if (Line.Contains(Needle))
		{
			++Count;
		}
	}
	return Count;
}
}

bool FMantlePlaceImportTimingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportTiming;

	// --- One phase, one row ---------------------------------------------------------------------
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);
		Timeline.Record(Phase::IntegrityPrecheck, 2.0);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("one row per phase: zip"), RowsMentioning(Summary, Phase::ZipAndManifest), 1);
		TestEqual(TEXT("one row per phase: integrity"), RowsMentioning(Summary, Phase::IntegrityPrecheck), 1);
	}

	// --- Exactly one block ----------------------------------------------------------------------
	// The opening rule and the TOTAL row are what a reader scans for; two of either means two
	// imports got interleaved, which is the state a baseline cannot be taken in.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Wipe, 0.5, TEXT("assets"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("one header"), RowsMentioning(Summary, TEXT("Mantle Place import timing")), 1);
		TestEqual(TEXT("one total"), RowsMentioning(Summary, TEXT("TOTAL (wall)")), 1);
	}

	// --- A repeated phase is distinguished by its detail, not by a second phase name -------------
	// Per-artifact lines all share one phase name on purpose: the vocabulary stays small enough to
	// diff, and which artifact it was is data rather than a new phase.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Artifact, 1.0, TEXT("terrain mesh (glTF, Nanite)"));
		Timeline.Record(Phase::Artifact, 2.0, TEXT("buildings mesh (glTF)"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("two artifact rows"), RowsMentioning(Summary, Phase::Artifact), 2);
		TestEqual(TEXT("the first names its artifact"), RowsMentioning(Summary, TEXT("terrain mesh")), 1);
		TestEqual(TEXT("the second names its artifact"), RowsMentioning(Summary, TEXT("buildings mesh")), 1);
	}

	// --- The three hotspots each get their own row ----------------------------------------------
	// The whole reason the baseline is being taken: their share of the total has to be readable
	// without a profiler.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ShaderStall, 30.0);
		Timeline.Record(Phase::CesiumAvailability, 5.0, TEXT("400 entries"));
		Timeline.Record(Phase::WeightResample, 8.0, TEXT("2 plane(s)"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("shader stall row"), RowsMentioning(Summary, Phase::ShaderStall), 1);
		TestEqual(TEXT("cesium rewrite row"), RowsMentioning(Summary, Phase::CesiumAvailability), 1);
		TestEqual(TEXT("weight resample row"), RowsMentioning(Summary, Phase::WeightResample), 1);
	}

	// --- A phase that did not run has no row ----------------------------------------------------
	// An absent row means "not reached", which is information. A zero would read as "instant".
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("an unrun phase has no row"), RowsMentioning(Summary, Phase::ShaderStall), 0);
	}

	// --- The total is wall time, not the sum of the phases ---------------------------------------
	// The phases do not tile the import, so printing their sum as the total would quietly claim
	// everything is accounted for. The unmeasured remainder is printed instead, and said out loud.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1000.0); // far more than the wall time of this test

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("the remainder is named"), RowsMentioning(Summary, TEXT("unmeasured")), 1);
		TestTrue(TEXT("wall time is not the sum"), Timeline.ElapsedSeconds() < 1000.0);
	}

	// --- RecordOnCurrent outside an import is a no-op --------------------------------------------
	// The landscape importer is also driven by tests, where no import is running. A timing call
	// must never be the reason one of those crashes.
	{
		TestNull(TEXT("no import is current here"), Current());
		RecordOnCurrent(Phase::ShaderStall, 1.0); // must not crash
	}

	// --- FScopedCurrent publishes, and restores rather than clearing -----------------------------
	{
		FTimeline Outer;
		Outer.Start();
		{
			FScopedCurrent PublishOuter(Outer);
			TestEqual(TEXT("the outer timeline is current"), Current(), &Outer);

			FTimeline Inner;
			Inner.Start();
			{
				FScopedCurrent PublishInner(Inner);
				TestEqual(TEXT("the inner timeline takes over"), Current(), &Inner);
				RecordOnCurrent(Phase::ShaderStall, 3.0);
			}
			// Restoring rather than clearing is what keeps a nested import from silently
			// un-instrumenting the one around it.
			TestEqual(TEXT("the outer timeline is restored"), Current(), &Outer);
			TestEqual(TEXT("the inner timeline got the record"), Inner.GetEntries().Num(), 1);
		}
		TestNull(TEXT("nothing is current after the outermost scope"), Current());
		TestEqual(TEXT("the outer timeline got nothing"), Outer.GetEntries().Num(), 0);
	}

	// --- FScopedPhase records on the way out, on whichever path leaves the scope -----------------
	{
		FTimeline Timeline;
		Timeline.Start();
		{
			const FScopedPhase Measured(Timeline, Phase::Wipe, TEXT("assets"));
			TestEqual(TEXT("nothing is recorded until the scope ends"), Timeline.GetEntries().Num(), 0);
		}
		TestEqual(TEXT("exactly one entry after the scope"), Timeline.GetEntries().Num(), 1);
		if (Timeline.GetEntries().Num() == 1)
		{
			TestEqual(TEXT("it carries its detail"), Timeline.GetEntries()[0].Detail, FString(TEXT("assets")));
			TestTrue(TEXT("it carries a non-negative duration"), Timeline.GetEntries()[0].Seconds >= 0.0);
		}
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
