// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * Where one step of a bundle import stands, in the words its row says (HPS-51): Waiting, Importing,
 * Done, Failed, Cancelled, Not Run.
 *
 * Cancelled and Not Run are different facts (HPS-51): Cancelled is a step a cancel stopped partway,
 * which left work in the project that a re-import replaces; Not Run is a step that never started,
 * which left nothing. A step the import skipped for a stated reason (what it is made from is not in
 * the archive) left nothing too, so it is Not Run, with the reason beside it.
 */
enum class EMantlePlaceImportStepState : uint8
{
	Waiting,
	Importing,
	Done,
	Failed,
	Cancelled,
	NotRun,
};

/** One row of the import window: the step, and where it stands. */
struct FMantlePlaceImportStep
{
	/** What the import knows the step by (an artifact id, or one of the fixed steps). */
	FName Id;
	/** The row's name: what the step builds. */
	FString Name;
	EMantlePlaceImportStepState State = EMantlePlaceImportStepState::Waiting;
	/** Why a step that did not run did not, said beside it. Empty when there is nothing to say. */
	FString Reason;
	/**
	 * Some of the step's work has run (a handler's Prepare half, which runs before the undo transaction
	 * opens), so a cancel before the rest leaves it Cancelled rather than Not Run.
	 */
	bool bStarted = false;
	/** When the step last started, and when it ended; seconds on the caller's clock. */
	double StartedAt = 0.0;
	double EndedAt = 0.0;
};

/**
 * One bundle import's steps, their states, and a cancel that takes effect between steps: the decision
 * the import window shows and the importer obeys, with no editor and no Slate, so a headless test
 * reads every transition.
 *
 * The importer asks Begin before each step's work. With no cancel waiting, the step reads Importing
 * and the work runs; with one waiting, Begin answers false and the step is stopped where it stands:
 * Cancelled if part of it already ran, Not Run if none did. A cancel never interrupts a step: the
 * running step finishes, and only the steps after it do not start.
 *
 * Every transition calls OnChanged, which is how the window repaints while the editor's thread is busy
 * importing, and how a click on its Cancel reaches RequestCancel between two steps.
 */
class FMantlePlaceImportSteps
{
public:
	/** Called after every change, on the thread that made it. */
	TFunction<void()> OnChanged;

	/** Append a step, Waiting. Returns its index; an id already listed returns the existing index. */
	int32 Add(FName Id, const FString& Name);

	/** The step's index, or INDEX_NONE. */
	int32 Find(FName Id) const;

	const TArray<FMantlePlaceImportStep>& Steps() const { return StepList; }

	/** Not Run, for a stated reason, before anything started: the import will not run it. */
	void Skip(FName Id, const FString& Reason);

	/**
	 * The boundary before a step's work. True, and the step Importing, when no cancel is waiting (or
	 * the id is not listed: an unlisted step is not the window's to stop). False when one is: the step
	 * is then Cancelled if part of it had run and Not Run if none had, and its work must not start.
	 */
	bool Begin(FName Id, double Now);

	/** The first part of a step's work has run and the rest comes later: Waiting again, but started. */
	void Pause(FName Id, double Now);

	/** The step's work is over: Done, or Failed. */
	void End(FName Id, bool bSucceeded, double Now);

	/** What the step in flight is doing now, said after its name ("45%"). Empty to say nothing. */
	void SetActivity(const FString& InActivity);

	/** Ask for the import to stop at the next step boundary. Ignored once the run is finished. */
	void RequestCancel();
	bool IsCancelRequested() const { return bCancelRequested; }

	/**
	 * The run is over, whichever way. A step still Importing is Failed (or Cancelled when a cancel is
	 * waiting: the run stopped inside it); a Waiting one is Not Run, or Cancelled if part of it ran and
	 * a cancel is what ended the run.
	 */
	void Finish(double Now);
	bool IsFinished() const { return bFinished; }

	/** A cancel took effect: some step was stopped, or never started, because of it. */
	bool WasCancelled() const;

	/** The step Importing now, or INDEX_NONE. */
	int32 CurrentIndex() const;

	/**
	 * Whether the editor's thread is inside the importer's synchronous run, where the window must take
	 * input for itself alone between steps (SMantlePlaceImportWindow says why).
	 */
	void SetSynchronous(bool bInSynchronous);
	bool IsSynchronous() const { return bSynchronous; }

	/** The run's own clock: when the first step began, and when Finish was called (0 while running). */
	double RunStartedAt() const { return RunStarted; }
	double RunEndedAt() const { return RunEnded; }

	/**
	 * The import window's heading, its title bar, its stop button and the word before the run's clock:
	 * HPS-51's words for this window (HPS-60).
	 */
	static FString Heading() { return TEXT("Bundle Import"); }
	static FString WindowTitle() { return TEXT("Mantle Place Bundle Import"); }
	static FString CancelWord() { return TEXT("Cancel"); }
	static FString ElapsedWord() { return TEXT("Elapsed"); }

	/** A state, as its row says it (HPS-51). */
	static FString StateWord(EMantlePlaceImportStepState State);

	/**
	 * How long something has run: `12 s`, `4 min 10 s`, `1 h 5 min`. Whole seconds, truncated, so the
	 * clock never claims a minute it has not reached; past an hour the seconds are dropped. A negative
	 * span reads as nothing elapsed. The same clock the Revit import window reads.
	 */
	static FString Elapsed(double Seconds);

	/**
	 * The window's status line: the step in flight, what it is doing, how long it has run, and a cancel
	 * that is waiting for the step to end. Empty between steps and once the run is over.
	 */
	FString StatusLine(double Now) const;

	/**
	 * How long the whole run has run, or ran, as HPS-60 shows it: `Elapsed 4 min 10 s`, a clock that runs
	 * while the import runs and stops when it ends. Empty before the first step.
	 */
	FString RunElapsed(double Now) const;

	/** The closing line of a cancelled run: how far it got, and that importing again completes it. */
	FString DescribeCancel() const;

private:
	void Changed() const;
	void StopAt(FMantlePlaceImportStep& Step);

	TArray<FMantlePlaceImportStep> StepList;
	FString Activity;
	bool bCancelRequested = false;
	bool bFinished = false;
	bool bSynchronous = false;
	bool bRunStarted = false;
	double RunStarted = 0.0;
	double RunEnded = 0.0;
};
