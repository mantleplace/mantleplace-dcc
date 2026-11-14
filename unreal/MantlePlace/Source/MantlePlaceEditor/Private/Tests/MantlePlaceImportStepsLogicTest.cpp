// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceImportTiming.h"

// The import window's steps, with no editor and no bundle: the six states and their words (HPS-51),
// the clock, the status line, a skip with its reason, and a cancel that takes effect between steps --
// the running step finishes, the later ones do not start, and a step part of which had run reads
// Cancelled rather than Not Run. Then the same cancel through the orchestration's walk of the handlers,
// which is what decides which handlers' work runs.

namespace
{
using EState = EMantlePlaceImportStepState;

FString States(const FMantlePlaceImportSteps& Steps)
{
	TArray<FString> Words;
	for (const FMantlePlaceImportStep& Step : Steps.Steps())
	{
		Words.Add(Step.Id.ToString() + TEXT("=") + FMantlePlaceImportSteps::StateWord(Step.State));
	}
	return FString::Join(Words, TEXT(" "));
}

/** A handler that records its calls, optionally prepares, and optionally fails its import. */
class FStepRecordingHandler final : public IMantlePlaceArtifactHandler
{
public:
	FStepRecordingHandler(FName InId, TArray<FString>& InCalls, bool bInPrepares = false, bool bInFails = false,
		const FString& InPayload = FString())
		: IdValue(InId), Calls(InCalls), bPreparesValue(bInPrepares), bFails(bInFails), Payload(InPayload)
	{
	}

	FName Id() const override { return IdValue; }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::None; }
	FText DisplayName() const override { return FText::FromString(TEXT("Step ") + IdValue.ToString()); }
	bool ParseFields(const FMantlePlaceParseInput&, FMantlePlaceVaultManifest&, FString&) const override { return true; }
	bool IsPresent(const FMantlePlaceVaultManifest&) const override { return true; }
	void CollectDeclared(const FMantlePlaceVaultManifest&, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (!Payload.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ Payload, FString() });
		}
	}
	bool Prepares() const override { return bPreparesValue; }
	void Prepare(FMantlePlaceImportContext&) override
	{
		if (bPreparesValue)
		{
			Calls.Add(IdValue.ToString() + TEXT(".Prepare"));
		}
	}
	void Import(FMantlePlaceImportContext& Context) override
	{
		Calls.Add(IdValue.ToString() + TEXT(".Import"));
		if (bFails)
		{
			Context.bAllRequestedSucceeded = false;
		}
	}
	void Finalize(FMantlePlaceImportContext&) override { Calls.Add(IdValue.ToString() + TEXT(".Finalize")); }

private:
	FName IdValue;
	TArray<FString>& Calls;
	bool bPreparesValue;
	bool bFails;
	FString Payload;
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportStepsStatesTest,
	"MantlePlace.Import.Steps.States",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportStepsStatesTest::RunTest(const FString& Parameters)
{
	// --- The window's words: HPS-51's heading and stop button, and the six a row says ------------------
	TestEqual(TEXT("the heading"), FMantlePlaceImportSteps::Heading(), FString(TEXT("Bundle Import")));
	TestEqual(TEXT("the stop button"), FMantlePlaceImportSteps::CancelWord(), FString(TEXT("Cancel")));
	TestEqual(TEXT("the word before the run's clock"), FMantlePlaceImportSteps::ElapsedWord(), FString(TEXT("Elapsed")));
	TestTrue(TEXT("the title bar names the window by the heading"), FMantlePlaceImportSteps::WindowTitle().EndsWith(TEXT("Bundle Import")));
	TestEqual(TEXT("Waiting"), FMantlePlaceImportSteps::StateWord(EState::Waiting), TEXT("Waiting"));
	TestEqual(TEXT("Importing"), FMantlePlaceImportSteps::StateWord(EState::Importing), TEXT("Importing"));
	TestEqual(TEXT("Done"), FMantlePlaceImportSteps::StateWord(EState::Done), TEXT("Done"));
	TestEqual(TEXT("Failed"), FMantlePlaceImportSteps::StateWord(EState::Failed), TEXT("Failed"));
	TestEqual(TEXT("Cancelled"), FMantlePlaceImportSteps::StateWord(EState::Cancelled), TEXT("Cancelled"));
	TestEqual(TEXT("Not Run"), FMantlePlaceImportSteps::StateWord(EState::NotRun), TEXT("Not Run"));

	// --- The clock: whole seconds, truncated; minutes past a minute; no seconds past an hour ------------
	TestEqual(TEXT("under a minute"), FMantlePlaceImportSteps::Elapsed(12.9), TEXT("12 s"));
	TestEqual(TEXT("minutes and seconds"), FMantlePlaceImportSteps::Elapsed(250.0), TEXT("4 min 10 s"));
	TestEqual(TEXT("hours and minutes"), FMantlePlaceImportSteps::Elapsed(3900.0), TEXT("1 h 5 min"));
	TestEqual(TEXT("a negative span reads as nothing elapsed"), FMantlePlaceImportSteps::Elapsed(-3.0), TEXT("0 s"));

	// --- A run, step by step ---------------------------------------------------------------------------
	FMantlePlaceImportSteps Steps;
	int32 Changes = 0;
	Steps.OnChanged = [&Changes]() { ++Changes; };

	TestEqual(TEXT("the first step is index 0"), Steps.Add(TEXT("a"), TEXT("Imagery drape")), 0);
	Steps.Add(TEXT("b"), TEXT("Landscape"));
	Steps.Add(TEXT("c"), TEXT("Buildings"));
	Steps.Add(TEXT("d"), TEXT("Public records"));
	TestEqual(TEXT("an id already listed is not listed twice"), Steps.Add(TEXT("b"), TEXT("Landscape")), 1);
	TestEqual(TEXT("four rows"), Steps.Steps().Num(), 4);
	TestEqual(TEXT("every row starts Waiting"), States(Steps), TEXT("a=Waiting b=Waiting c=Waiting d=Waiting"));
	TestTrue(TEXT("each change is announced, so the window repaints"), Changes >= 4);
	TestTrue(TEXT("no status line before a step runs"), Steps.StatusLine(0.0).IsEmpty());
	TestTrue(TEXT("no run clock before a step runs"), Steps.RunElapsed(0.0).IsEmpty());

	Steps.Skip(TEXT("d"), TEXT("Not in this download: it was left out of the selection."));
	TestEqual(TEXT("a skipped step is Not Run"), Steps.Steps()[3].State, EState::NotRun);
	TestEqual(TEXT("and says why beside it"), Steps.Steps()[3].Reason,
		FString(TEXT("Not in this download: it was left out of the selection.")));

	TestTrue(TEXT("Begin with no cancel waiting runs the step"), Steps.Begin(TEXT("a"), 100.0));
	TestEqual(TEXT("the running step is current"), Steps.CurrentIndex(), 0);
	TestEqual(TEXT("and reads Importing"), Steps.Steps()[0].State, EState::Importing);
	TestEqual(TEXT("the status line names the step and its clock"), Steps.StatusLine(112.0), FString(TEXT("Imagery drape: 12 s")));
	Steps.SetActivity(TEXT("45%"));
	TestEqual(TEXT("with what it is doing when that is known"), Steps.StatusLine(112.0), FString(TEXT("Imagery drape: 45%, 12 s")));
	Steps.End(TEXT("a"), true, 113.0);
	TestEqual(TEXT("a step that finished is Done"), Steps.Steps()[0].State, EState::Done);
	TestTrue(TEXT("no status line between steps"), Steps.StatusLine(113.0).IsEmpty());

	TestTrue(TEXT("b begins"), Steps.Begin(TEXT("b"), 113.0));
	Steps.End(TEXT("b"), false, 120.0);
	TestEqual(TEXT("a step whose work failed is Failed"), Steps.Steps()[1].State, EState::Failed);
	TestFalse(TEXT("a step that is over does not begin again"), Steps.Begin(TEXT("b"), 121.0));
	TestFalse(TEXT("nor does a skipped one"), Steps.Begin(TEXT("d"), 121.0));
	TestTrue(TEXT("an unlisted step is not the window's to stop"), Steps.Begin(TEXT("unlisted"), 121.0));

	TestTrue(TEXT("c begins its first half"), Steps.Begin(TEXT("c"), 121.0));
	Steps.Pause(TEXT("c"), 125.0);
	TestEqual(TEXT("between its halves the step is Waiting again"), Steps.Steps()[2].State, EState::Waiting);
	TestTrue(TEXT("but marked started"), Steps.Steps()[2].bStarted);

	// --- The run ends without a cancel: a step left Waiting never ran ---------------------------------------
	Steps.Finish(130.0);
	TestTrue(TEXT("the run is finished"), Steps.IsFinished());
	TestEqual(TEXT("the run clock stops at Finish, after HPS-60's word"), Steps.RunElapsed(500.0), FString(TEXT("Elapsed 30 s")));
	TestEqual(TEXT("with no cancel, a step the run ended before finishing is Not Run: Cancelled is a cancel's word"),
		Steps.Steps()[2].State, EState::NotRun);
	TestFalse(TEXT("no cancel was asked for, so the run was not cancelled"), Steps.WasCancelled());
	Steps.RequestCancel();
	TestFalse(TEXT("a cancel after the run is over is ignored"), Steps.IsCancelRequested());

	// --- A run that stops inside a step, with no cancel: the step failed ------------------------------------
	FMantlePlaceImportSteps Refused;
	Refused.Add(TEXT("integrity_check"), TEXT("Integrity Check"));
	Refused.Add(TEXT("a"), TEXT("Imagery drape"));
	Refused.Begin(TEXT("integrity_check"), 0.0);
	Refused.Finish(1.0);
	TestEqual(TEXT("a run that returned inside a step failed it, and the rest never ran"),
		States(Refused), TEXT("integrity_check=Failed a=Not Run"));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportStepsCancelTest,
	"MantlePlace.Import.Steps.Cancel",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportStepsCancelTest::RunTest(const FString& Parameters)
{
	// --- Cancel between steps: the running step finishes, the later ones do not start -------------------
	{
		FMantlePlaceImportSteps Steps;
		Steps.Add(TEXT("a"), TEXT("Imagery drape"));
		Steps.Add(TEXT("b"), TEXT("Buildings"));
		Steps.Add(TEXT("c"), TEXT("Landscape"));
		Steps.Add(TEXT("d"), TEXT("Public records"));
		Steps.Skip(TEXT("d"), TEXT("Not in this download: it was left out of the selection."));

		// b's first half (a Prepare) runs before a: it is started when the cancel comes.
		Steps.Begin(TEXT("b"), 0.0);
		Steps.Pause(TEXT("b"), 1.0);
		Steps.Begin(TEXT("a"), 1.0);
		Steps.RequestCancel();
		TestTrue(TEXT("the cancel is waiting"), Steps.IsCancelRequested());
		TestEqual(TEXT("the status line says the cancel waits for the step to end"),
			Steps.StatusLine(3.0), FString(TEXT("Imagery drape: 2 s. Cancelling when this step ends.")));
		TestEqual(TEXT("the running step is not interrupted"), Steps.Steps()[0].State, EState::Importing);
		Steps.End(TEXT("a"), true, 4.0);
		TestEqual(TEXT("the running step finishes Done"), Steps.Steps()[0].State, EState::Done);

		TestFalse(TEXT("the next boundary stops b"), Steps.Begin(TEXT("b"), 4.0));
		TestFalse(TEXT("and c"), Steps.Begin(TEXT("c"), 4.0));
		TestEqual(TEXT("a step part of which ran is Cancelled; one that never started is Not Run"),
			States(Steps), TEXT("a=Done b=Cancelled c=Not Run d=Not Run"));
		Steps.Finish(5.0);
		TestTrue(TEXT("the run was cancelled"), Steps.WasCancelled());
		const FString Closing = Steps.DescribeCancel();
		TestTrue(TEXT("the closing line counts what was done"), Closing.Contains(TEXT("1 step(s) done")));
		TestTrue(TEXT("and what never ran, not counting the skipped step"), Closing.Contains(TEXT("1 not run")));
		TestTrue(TEXT("and what was stopped partway"), Closing.Contains(TEXT("1 cancelled partway")));
		TestTrue(TEXT("and says importing again completes it"), Closing.Contains(TEXT("importing this bundle again")));
	}

	// --- A cancel the run ends on inside a step (the download): that step was stopped partway -------------
	{
		FMantlePlaceImportSteps Steps;
		Steps.Add(TEXT("download"), TEXT("Download"));
		Steps.Begin(TEXT("download"), 0.0);
		Steps.SetActivity(TEXT("45%"));
		Steps.RequestCancel();
		Steps.Finish(2.0);
		TestEqual(TEXT("the download a cancel stopped is Cancelled"), Steps.Steps()[0].State, EState::Cancelled);
		TestTrue(TEXT("the run was cancelled"), Steps.WasCancelled());
		TestTrue(TEXT("no status line once the run is over"), Steps.StatusLine(3.0).IsEmpty());
	}

	// --- A cancel that came after the last step stopped nothing ------------------------------------------
	{
		FMantlePlaceImportSteps Steps;
		Steps.Add(TEXT("a"), TEXT("Landscape"));
		Steps.Begin(TEXT("a"), 0.0);
		Steps.RequestCancel();
		Steps.End(TEXT("a"), true, 1.0);
		Steps.Finish(1.0);
		TestFalse(TEXT("every step finished, so the import was not cancelled"), Steps.WasCancelled());
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportStepsOrchestrationTest,
	"MantlePlace.Import.Orchestration.StepsAndCancel",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportStepsOrchestrationTest::RunTest(const FString& Parameters)
{
	// The registry's two passes with steps watching: a (prepares), b, c (prepares), d (fails), and e,
	// whose only payload the archive's view leaves out.
	const auto Build = [](TArray<FString>& Calls)
	{
		MantlePlaceArtifacts::FHandlers Handlers;
		Handlers.Add(MakeUnique<FStepRecordingHandler>(FName(TEXT("a")), Calls, true));
		Handlers.Add(MakeUnique<FStepRecordingHandler>(FName(TEXT("b")), Calls));
		Handlers.Add(MakeUnique<FStepRecordingHandler>(FName(TEXT("c")), Calls, true));
		Handlers.Add(MakeUnique<FStepRecordingHandler>(FName(TEXT("d")), Calls, false, true));
		Handlers.Add(MakeUnique<FStepRecordingHandler>(FName(TEXT("e")), Calls, false, false, TEXT("Records/Parcels.gpkg")));
		return Handlers;
	};
	FMantlePlaceVaultManifest Manifest;
	Manifest.Archive.bDeclared = true;
	Manifest.Archive.Kind = TEXT("selection");
	Manifest.Archive.Parts = { TEXT("mesh.glb@MAX") };
	Manifest.Archive.FileParts.Add(TEXT("Records/Parcels.gpkg"), TEXT("records.parcels_gpkg@MAX"));

	FMantlePlaceImportSelection Selection;
	Selection.Excluded.Add(FName(TEXT("excluded")));

	// Runs the three passes, cancelling when the step named CancelWhen begins (NAME_None: never).
	const auto Run = [&](FName CancelWhen, FMantlePlaceImportSteps& Steps, TArray<FString>& Calls)
	{
		const MantlePlaceArtifacts::FHandlers Handlers = Build(Calls);
		MantlePlaceImportOrchestration::PlanSteps(Handlers, Manifest, Selection, Steps);
		Steps.OnChanged = [&Steps, CancelWhen]()
		{
			const int32 Current = Steps.CurrentIndex();
			if (Current != INDEX_NONE && Steps.Steps()[Current].Id == CancelWhen)
			{
				Steps.RequestCancel(); // the Cancel click a Slate tick delivers at this boundary
			}
		};
		MantlePlaceImportTiming::FTimeline Timeline;
		FMantlePlaceImportResult Result;
		TArray<FString> Log;
		FMantlePlaceImportContext Context(nullptr, Manifest, nullptr, Selection, Timeline, Result, Log);
		Context.Steps = &Steps;
		MantlePlaceImportOrchestration::RunPrepare(Handlers, Context);
		MantlePlaceImportOrchestration::RunImport(Handlers, Context);
		MantlePlaceImportOrchestration::RunFinalize(Handlers, Context);
		Steps.Finish(0.0);
		return Log;
	};

	// --- The plan: one row per artifact, named for what it builds; a left-out one Not Run, with why -------
	{
		FMantlePlaceImportSteps Steps;
		TArray<FString> Calls;
		const TArray<FString> Log = Run(NAME_None, Steps, Calls);
		TestEqual(TEXT("one row per handler, in registry order"), Steps.Steps().Num(), 5);
		TestEqual(TEXT("a row is named by its handler"), Steps.Steps()[0].Name, FString(TEXT("Step a")));
		TestEqual(TEXT("no cancel: every step runs; the failing one Failed; the left-out one Not Run"),
			States(Steps), TEXT("a=Done b=Done c=Done d=Failed e=Not Run"));
		TestFalse(TEXT("the left-out step says why"), Steps.Steps()[4].Reason.IsEmpty());
		TestEqual(TEXT("the walk is unchanged by watching it"), FString::Join(Calls, TEXT(" ")),
			TEXT("a.Prepare c.Prepare a.Import b.Import c.Import d.Import a.Finalize b.Finalize c.Finalize d.Finalize"));
		TestFalse(TEXT("an import nobody cancelled was not cancelled"), Steps.WasCancelled());
	}

	// --- Cancel while b imports: b finishes, c (prepared) is Cancelled, d never runs ----------------------
	{
		FMantlePlaceImportSteps Steps;
		TArray<FString> Calls;
		const TArray<FString> Log = Run(FName(TEXT("b")), Steps, Calls);
		TestEqual(TEXT("the steps after the running one stop where they stand"),
			States(Steps), TEXT("a=Done b=Done c=Cancelled d=Not Run e=Not Run"));
		TestEqual(TEXT("b's import finished; nothing after it imported; only what imported is finalized"),
			FString::Join(Calls, TEXT(" ")),
			TEXT("a.Prepare c.Prepare a.Import b.Import a.Finalize b.Finalize"));
		TestTrue(TEXT("the import was cancelled"), Steps.WasCancelled());
		TestFalse(TEXT("a cut handler is not logged as left out of the archive"),
			FString::Join(Log, TEXT("\n")).Contains(TEXT("Step c skipped")));
	}

	// --- Cancel during the first Prepare: that half finishes, nothing else runs -----------------------------
	{
		FMantlePlaceImportSteps Steps;
		TArray<FString> Calls;
		Run(FName(TEXT("a")), Steps, Calls);
		TestEqual(TEXT("a's prepared half finished, and nothing more of any step ran"),
			FString::Join(Calls, TEXT(" ")), TEXT("a.Prepare"));
		TestEqual(TEXT("a ran part of itself and is Cancelled; the rest are Not Run"),
			States(Steps), TEXT("a=Cancelled b=Not Run c=Not Run d=Not Run e=Not Run"));
	}
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
