// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportTiming.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

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

/** Parse a record, or return null. A record that does not parse is the failure worth naming. */
TSharedPtr<FJsonObject> ParseRecord(const FString& Json)
{
	TSharedPtr<FJsonObject> Object;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	return FJsonSerializer::Deserialize(Reader, Object) ? Object : nullptr;
}

/** A directory under this machine's temp tree that no other test writes into. */
FString ScratchDirectory()
{
	return FPaths::Combine(FPlatformProcess::UserTempDir(),
	                       FString::Printf(TEXT("MantlePlaceTimelineTest-%s"),
	                                       *FGuid::NewGuid().ToString(EGuidFormats::Digits)));
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

	// --- The record is valid JSON, and says which shape it is -----------------------------------
	// A consumer has to be able to refuse a shape it does not know, rather than read a field that
	// has moved. That is the whole job of the schema string.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);

		FRecordContext Context;
		Context.Bundle = TEXT("SomeBundle.zip");
		Context.bSucceeded = true;

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, Context));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestEqual(TEXT("it names its schema"), Record->GetStringField(TEXT("schema")), FString(RecordSchema));
			TestEqual(TEXT("it carries the bundle's name"), Record->GetStringField(TEXT("bundle")), FString(TEXT("SomeBundle.zip")));
			TestTrue(TEXT("it carries the outcome"), Record->GetBoolField(TEXT("succeeded")));
			TestTrue(TEXT("it is stamped"), !Record->GetStringField(TEXT("writtenAtUtc")).IsEmpty());
		}
	}

	// --- One entry per recorded phase, in the order recorded, with its detail and seconds --------
	// The same property the summary block has, asserted on the half a machine reads. A record that
	// collapsed two artifact rows into one would still look plausible in a log.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Artifact, 1.25, TEXT("terrain mesh (glTF, Nanite)"));
		Timeline.Record(Phase::Artifact, 2.50, TEXT("buildings mesh (glTF)"));

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			const TArray<TSharedPtr<FJsonValue>>& Phases = Record->GetArrayField(TEXT("phases"));
			TestEqual(TEXT("two phases recorded, two entries written"), Phases.Num(), 2);
			if (Phases.Num() == 2)
			{
				TestEqual(TEXT("the first keeps its phase"), Phases[0]->AsObject()->GetStringField(TEXT("phase")), FString(Phase::Artifact));
				TestEqual(TEXT("the first keeps its detail"), Phases[0]->AsObject()->GetStringField(TEXT("detail")), FString(TEXT("terrain mesh (glTF, Nanite)")));
				TestEqual(TEXT("the first keeps its seconds"), Phases[0]->AsObject()->GetNumberField(TEXT("seconds")), 1.25);
				TestEqual(TEXT("the second is the second"), Phases[1]->AsObject()->GetStringField(TEXT("detail")), FString(TEXT("buildings mesh (glTF)")));
			}
		}
	}

	// --- The total is wall time here too, not the sum of the phases ------------------------------
	// The log block says this out loud in a comment column a machine cannot read. The record has to
	// carry the same truth, because a consumer that summed `phases` would get a different number
	// from the one printed and no way to tell which one is the import's cost.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1000.0); // far more than this test's wall time

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestTrue(TEXT("the total is wall time, not the sum"), Record->GetNumberField(TEXT("totalWallSeconds")) < 1000.0);
		}
	}

	// --- A timeline with no phases is still a record ---------------------------------------------
	// An import that died before its first phase took a measurable amount of time doing so, and a
	// consumer told nothing at all cannot tell that from a run where no import happened.
	{
		FTimeline Timeline;
		Timeline.Start();

		FRecordContext Context;
		Context.bSucceeded = false;

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, Context));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestEqual(TEXT("no phases, no entries"), Record->GetArrayField(TEXT("phases")).Num(), 0);
			TestFalse(TEXT("a failed import says so"), Record->GetBoolField(TEXT("succeeded")));
		}
	}

	// --- The record carries no threshold, bar or target -------------------------------------------
	// This plugin measures and reports; it does not judge. Asserted rather than left to review
	// because the pressure to add an "expected" column to a timing record is constant, the edit
	// that does it is one line, and a reviewer reading a diff of JSON field names will wave it
	// through. What a good timeline is belongs to whoever consumes this file.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ShaderStall, 30.0);

		const FString Json = BuildRecord(Timeline, FRecordContext()).ToLower();
		const TCHAR* Judgements[] = { TEXT("threshold"), TEXT("baseline"), TEXT("budget"), TEXT("expected"), TEXT("limit"), TEXT("target") };
		for (const TCHAR* Judgement : Judgements)
		{
			TestFalse(*FString::Printf(TEXT("the record does not judge: '%s'"), Judgement), Json.Contains(Judgement));
		}
	}

	// --- No directory, no file, no complaint -----------------------------------------------------
	// The emitter is opt-in. A plugin that scatters JSON into a curator's project by default earns
	// a bug report about a folder nobody asked for.
	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Wipe, 0.5, TEXT("assets"));

		TestEqual(TEXT("nothing is written when nowhere is named"),
		          WriteRecord(Timeline, FRecordContext(), FString()), FString());
	}

	// --- Given a directory, it writes one file, and the file is the record ------------------------
	{
		const FString Directory = ScratchDirectory();

		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::IntegrityPrecheck, 0.6);

		FRecordContext Context;
		Context.Bundle = TEXT("LiveImportBundle.zip");
		Context.bSucceeded = true;

		const FString Written = WriteRecord(Timeline, Context, Directory);
		TestTrue(TEXT("it reports where it wrote"), !Written.IsEmpty());
		if (!Written.IsEmpty())
		{
			// The directory is created rather than required: a consumer naming a path that does
			// not exist yet is the normal case, not an error.
			TestTrue(TEXT("the file is there"), FPaths::FileExists(Written));

			FString Json;
			TestTrue(TEXT("the file reads back"), FFileHelper::LoadFileToString(Json, *Written));
			const TSharedPtr<FJsonObject> Record = ParseRecord(Json);
			TestNotNull(TEXT("what was written parses"), Record.Get());
			if (Record.IsValid())
			{
				TestEqual(TEXT("it is the same record"), Record->GetStringField(TEXT("bundle")), FString(TEXT("LiveImportBundle.zip")));
				TestEqual(TEXT("with its one phase"), Record->GetArrayField(TEXT("phases")).Num(), 1);
			}
		}

		// Two imports, two files. One import per run is today's shape, not a guarantee, and a
		// second import overwriting the first would leave a consumer grading the wrong one with
		// nothing to notice.
		const FString Second = WriteRecord(Timeline, Context, Directory);
		TestTrue(TEXT("the second import reports where it wrote"), !Second.IsEmpty());
		TestTrue(TEXT("and it did not overwrite the first"), Second != Written);

		TArray<FString> Found;
		IFileManager::Get().FindFiles(Found, *FPaths::Combine(Directory, TEXT("*.json")), true, false);
		TestEqual(TEXT("two records on disk"), Found.Num(), 2);

		IFileManager::Get().DeleteDirectory(*Directory, false, true);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
