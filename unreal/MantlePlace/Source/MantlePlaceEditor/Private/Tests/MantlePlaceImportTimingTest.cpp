// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "HAL/PlatformProcess.h"
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

	// --- FScopedCurrentPhase reaches the running import from another function -------------------
	// The form a phase measured outside ImportVaultPackage's own scope must use. The first cut of
	// the Cesium-streaming phase used the in-scope form in a function with no timeline in it and
	// did not compile; this pins the working shape so a later edit cannot quietly swap it back.
	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			{
				const FScopedCurrentPhase Measured(Phase::CesiumAvailability, TEXT("400 entries"));
			}
		}
		TestEqual(TEXT("it recorded against the running import"), Timeline.GetEntries().Num(), 1);
		if (Timeline.GetEntries().Num() == 1)
		{
			TestEqual(TEXT("it carries its detail"), Timeline.GetEntries()[0].Detail, FString(TEXT("400 entries")));
		}
	}

	// --- And outside an import it is a no-op, not a crash ----------------------------------------
	{
		TestNull(TEXT("no import is current here"), Current());
		const FScopedCurrentPhase Measured(Phase::CesiumAvailability);
	}

	// --- The out-of-scope form nests too ---------------------------------------------------------
	// It reaches the running import through the same timeline, so it has to open a level on the
	// same counter. A form that recorded without opening one would be the double-count again, just
	// arriving from another translation unit.
	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("terrain mesh (glTF, Nanite)"));
			{
				const FScopedCurrentPhase Measured(Phase::CesiumAvailability, TEXT("400 entries"));
			}
		}

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the rewrite is nested in the artifact"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the artifact phase is top level"), Timeline.GetEntries()[1].Depth, 0);
		}
	}

	// --- A phase recorded inside another is marked as nested, and counted once -------------------
	// The regression this pins: `transaction close` was declared at function scope and stayed alive
	// to the end of it, so it enclosed every phase recorded after it. Those rows were counted
	// twice — shares summed past 100% and the remainder printed negative — and a nested row moves
	// whenever a row it encloses moves, which is what denies the hotspots an independent share.
	//
	// OpenPhase/ClosePhase are the guards' own bookkeeping, used directly here to build a nesting
	// that is deterministic in seconds. A guard measures real time, which cannot be asserted on.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the enclosed row is deeper"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the enclosing row is top level"), Timeline.GetEntries()[1].Depth, 0);
		}

		// 10.0, not 13.0. The stall happened *inside* the landscape import; adding both would
		// charge the import for it twice.
		TestEqual(TEXT("an enclosed row is not counted again"), Timeline.AccountedSeconds(), 10.0);
	}

	// --- The block says which rows enclose which ------------------------------------------------
	// A column that sums past 100% is unusable, but so is one that silently drops the nested rows:
	// the shader stall is a hotspot and has to stay visible. It is printed, indented, and the row
	// that encloses it says so — the reader can see both the part and the whole.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		TestEqual(TEXT("the nested row encloses nothing"), Timeline.EnclosedRowCount(0), 0);
		TestEqual(TEXT("the enclosing row encloses one"), Timeline.EnclosedRowCount(1), 1);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("the stall is still a row"), RowsMentioning(Summary, Phase::ShaderStall), 1);
		TestEqual(TEXT("the enclosing row is named as such"), RowsMentioning(Summary, TEXT("encloses")), 1);

		// Indented past the top-level rows, which is what makes a nested row readable as one at a
		// glance rather than by counting percentages.
		int32 StallIndent = -1;
		int32 ArtifactIndent = -1;
		for (const FString& Line : Summary)
		{
			const int32 Indent = Line.Len() - Line.TrimStart().Len();
			if (Line.Contains(Phase::ShaderStall))
			{
				StallIndent = Indent;
			}
			else if (Line.Contains(TEXT("landscape 2017x2017")))
			{
				ArtifactIndent = Indent;
			}
		}
		TestTrue(TEXT("the nested row is indented further"), StallIndent > ArtifactIndent);
	}

	// --- Nesting is measured, not declared -------------------------------------------------------
	// Depth comes from how many guards are open when a row is recorded, so a call site does not
	// have to know it is nested — which matters because the one that actually is does not use a
	// guard at all: the shader stall is a bare RecordOnCurrent from the landscape importer, inside
	// the artifact phase that encloses it.
	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("landscape 2017x2017"));
			RecordOnCurrent(Phase::ShaderStall, 3.0);
		}

		TestEqual(TEXT("both rows were recorded"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the stall is nested without asking to be"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the artifact phase is top level"), Timeline.GetEntries()[1].Depth, 0);
		}
	}

	// --- The remainder is not negative, which is the symptom this all exists to remove -----------
	// The named test from the report: one assertion over a timeline whose phases nest. It needs a
	// real wall-time denominator, so it sleeps — the only number here that cannot be synthesised,
	// since `ElapsedSeconds()` reads the clock. The assertion is one-sided and the sleep only ever
	// makes the wall time longer, so a loaded machine cannot turn this red.
	{
		FTimeline Timeline;
		Timeline.Start();
		FPlatformProcess::Sleep(0.05f);

		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 0.03);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 0.04, TEXT("landscape 2017x2017"));

		// 0.05 − 0.04, not 0.05 − 0.07. Counting the nested row here is what printed −44.2% under
		// a line promising the remainder would be positive.
		const double Remainder = Timeline.ElapsedSeconds() - Timeline.AccountedSeconds();
		TestTrue(TEXT("the remainder is not negative"), Remainder >= 0.0);
	}

	// --- A guard that attached to no import must not close a level it never opened ---------------
	// The asymmetry worth pinning: resolve the running import on the way in AND again on the way
	// out, and a guard constructed before an import but destroyed during one pops a level it never
	// pushed. `ClosePhase` clamps rather than complains, so the damage is silent — every row after
	// it is stamped one level too shallow, and the double count returns.
	{
		FTimeline Timeline;
		Timeline.Start();

		TestNull(TEXT("no import is current yet"), Current());
		TOptional<FScopedCurrentPhase> Straggler;
		Straggler.Emplace(Phase::CesiumAvailability, TEXT("400 entries"));

		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("landscape 2017x2017"));

			// Destroyed while an import IS running, unlike when it was constructed.
			Straggler.Reset();

			RecordOnCurrent(Phase::ShaderStall, 3.0);
		}

		TestEqual(TEXT("the straggler recorded nothing"),
		          RowsMentioning(Timeline.BuildSummary(), Phase::CesiumAvailability), 0);
		TestEqual(TEXT("two rows, not three"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the stall is still nested"), Timeline.GetEntries()[0].Depth, 1);
		}
	}

	// --- A guard closes its own level before recording -------------------------------------------
	// Otherwise a phase would be stamped one level deeper than it ran, and every top-level row
	// would read as nested inside nothing.
	{
		FTimeline Timeline;
		Timeline.Start();
		{
			const FScopedPhase Outer(Timeline, Phase::Wipe, TEXT("assets"));
			{
				const FScopedPhase Inner(Timeline, Phase::Artifact, TEXT("buildings mesh (glTF)"));
			}
		}

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the inner guard is nested"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the outer guard is not"), Timeline.GetEntries()[1].Depth, 0);
		}
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
