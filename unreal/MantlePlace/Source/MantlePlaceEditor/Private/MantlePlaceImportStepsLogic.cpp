// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportStepsLogic.h"

int32 FMantlePlaceImportSteps::Add(FName Id, const FString& Name)
{
	const int32 Existing = Find(Id);
	if (Existing != INDEX_NONE)
	{
		return Existing;
	}
	FMantlePlaceImportStep Step;
	Step.Id = Id;
	Step.Name = Name;
	const int32 Index = StepList.Add(MoveTemp(Step));
	Changed();
	return Index;
}

int32 FMantlePlaceImportSteps::Find(FName Id) const
{
	return StepList.IndexOfByPredicate([Id](const FMantlePlaceImportStep& Step) { return Step.Id == Id; });
}

void FMantlePlaceImportSteps::Skip(FName Id, const FString& Reason)
{
	const int32 Index = Find(Id);
	if (Index == INDEX_NONE)
	{
		return;
	}
	StepList[Index].State = EMantlePlaceImportStepState::NotRun;
	StepList[Index].Reason = Reason;
	Changed();
}

void FMantlePlaceImportSteps::StopAt(FMantlePlaceImportStep& Step)
{
	Step.State = Step.bStarted ? EMantlePlaceImportStepState::Cancelled : EMantlePlaceImportStepState::NotRun;
}

bool FMantlePlaceImportSteps::Begin(FName Id, double Now)
{
	const int32 Index = Find(Id);
	if (Index == INDEX_NONE)
	{
		return true;
	}
	FMantlePlaceImportStep& Step = StepList[Index];
	if (bFinished || Step.State != EMantlePlaceImportStepState::Waiting)
	{
		// Skipped, already stopped by an earlier boundary, or already over: its work does not start.
		return false;
	}
	if (bCancelRequested)
	{
		StopAt(Step);
		Changed();
		return false;
	}
	if (!bRunStarted)
	{
		bRunStarted = true;
		RunStarted = Now;
	}
	Step.State = EMantlePlaceImportStepState::Importing;
	Step.bStarted = true;
	Step.StartedAt = Now;
	Activity.Reset();
	Changed();
	return true;
}

void FMantlePlaceImportSteps::Pause(FName Id, double Now)
{
	const int32 Index = Find(Id);
	if (Index == INDEX_NONE || StepList[Index].State != EMantlePlaceImportStepState::Importing)
	{
		return;
	}
	StepList[Index].State = EMantlePlaceImportStepState::Waiting;
	StepList[Index].EndedAt = Now;
	Activity.Reset();
	Changed();
}

void FMantlePlaceImportSteps::End(FName Id, bool bSucceeded, double Now)
{
	const int32 Index = Find(Id);
	if (Index == INDEX_NONE || StepList[Index].State != EMantlePlaceImportStepState::Importing)
	{
		return;
	}
	StepList[Index].State = bSucceeded ? EMantlePlaceImportStepState::Done : EMantlePlaceImportStepState::Failed;
	StepList[Index].EndedAt = Now;
	Activity.Reset();
	Changed();
}

void FMantlePlaceImportSteps::SetActivity(const FString& InActivity)
{
	if (Activity == InActivity)
	{
		return;
	}
	Activity = InActivity;
	Changed();
}

void FMantlePlaceImportSteps::RequestCancel()
{
	if (bFinished || bCancelRequested)
	{
		return;
	}
	bCancelRequested = true;
	Changed();
}

void FMantlePlaceImportSteps::Finish(double Now)
{
	if (bFinished)
	{
		return;
	}
	for (FMantlePlaceImportStep& Step : StepList)
	{
		if (Step.State == EMantlePlaceImportStepState::Importing)
		{
			Step.State = bCancelRequested ? EMantlePlaceImportStepState::Cancelled : EMantlePlaceImportStepState::Failed;
			Step.EndedAt = Now;
		}
		else if (Step.State == EMantlePlaceImportStepState::Waiting)
		{
			// Cancelled is a cancel's word only: a run that ended on a failure left the rest Not Run.
			if (bCancelRequested)
			{
				StopAt(Step);
			}
			else
			{
				Step.State = EMantlePlaceImportStepState::NotRun;
			}
		}
	}
	bFinished = true;
	bSynchronous = false;
	RunEnded = Now;
	Activity.Reset();
	Changed();
}

bool FMantlePlaceImportSteps::WasCancelled() const
{
	if (!bCancelRequested)
	{
		return false;
	}
	return StepList.ContainsByPredicate([](const FMantlePlaceImportStep& Step)
	{
		return Step.State == EMantlePlaceImportStepState::Cancelled
			|| (Step.State == EMantlePlaceImportStepState::NotRun && Step.Reason.IsEmpty());
	});
}

int32 FMantlePlaceImportSteps::CurrentIndex() const
{
	return StepList.IndexOfByPredicate([](const FMantlePlaceImportStep& Step)
	{
		return Step.State == EMantlePlaceImportStepState::Importing;
	});
}

void FMantlePlaceImportSteps::SetSynchronous(bool bInSynchronous)
{
	if (bSynchronous == bInSynchronous)
	{
		return;
	}
	bSynchronous = bInSynchronous;
	Changed();
}

FString FMantlePlaceImportSteps::StateWord(EMantlePlaceImportStepState State)
{
	switch (State)
	{
	case EMantlePlaceImportStepState::Waiting: return TEXT("Waiting");
	case EMantlePlaceImportStepState::Importing: return TEXT("Importing");
	case EMantlePlaceImportStepState::Done: return TEXT("Done");
	case EMantlePlaceImportStepState::Failed: return TEXT("Failed");
	case EMantlePlaceImportStepState::Cancelled: return TEXT("Cancelled");
	case EMantlePlaceImportStepState::NotRun: return TEXT("Not Run");
	}
	return FString();
}

FString FMantlePlaceImportSteps::Elapsed(double Seconds)
{
	const int64 Whole = FMath::Max<int64>(0, static_cast<int64>(Seconds));
	if (Whole < 60)
	{
		return FString::Printf(TEXT("%lld s"), Whole);
	}
	if (Whole < 3600)
	{
		return FString::Printf(TEXT("%lld min %lld s"), Whole / 60, Whole % 60);
	}
	return FString::Printf(TEXT("%lld h %lld min"), Whole / 3600, Whole % 3600 / 60);
}

FString FMantlePlaceImportSteps::StatusLine(double Now) const
{
	const int32 Index = CurrentIndex();
	if (bFinished || Index == INDEX_NONE)
	{
		return FString();
	}
	const FMantlePlaceImportStep& Step = StepList[Index];
	FString Line = Step.Name + TEXT(": ");
	if (!Activity.IsEmpty())
	{
		Line += Activity + TEXT(", ");
	}
	Line += Elapsed(Now - Step.StartedAt);
	if (bCancelRequested)
	{
		// The running step is not interrupted: it finishes, and only the steps after it do not start.
		Line += TEXT(". Cancelling when this step ends.");
	}
	return Line;
}

FString FMantlePlaceImportSteps::RunElapsed(double Now) const
{
	if (!bRunStarted)
	{
		return FString();
	}
	return ElapsedWord() + TEXT(" ") + Elapsed((bFinished ? RunEnded : Now) - RunStarted);
}

FString FMantlePlaceImportSteps::DescribeCancel() const
{
	int32 Done = 0;
	int32 Stopped = 0;
	int32 NotRun = 0;
	for (const FMantlePlaceImportStep& Step : StepList)
	{
		Done += Step.State == EMantlePlaceImportStepState::Done ? 1 : 0;
		Stopped += Step.State == EMantlePlaceImportStepState::Cancelled ? 1 : 0;
		NotRun += Step.State == EMantlePlaceImportStepState::NotRun && Step.Reason.IsEmpty() ? 1 : 0;
	}
	FString Line = FString::Printf(TEXT("Import cancelled: %d step(s) done, %d not run"), Done, NotRun);
	if (Stopped > 0)
	{
		Line += FString::Printf(TEXT(", %d cancelled partway"), Stopped);
	}
	return Line + TEXT(". What finished stays in the level; importing this bundle again replaces it and completes the import.");
}

void FMantlePlaceImportSteps::Changed() const
{
	if (OnChanged)
	{
		OnChanged();
	}
}
