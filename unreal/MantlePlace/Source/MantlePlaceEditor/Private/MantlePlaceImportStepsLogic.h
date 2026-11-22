// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

enum class EMantlePlaceImportStepState : uint8
{
	Waiting,
	Importing,
	Done,
	Failed,
	Cancelled,
	NotRun,
};

struct FMantlePlaceImportStep
{
	FName Id;
	FString Name;
	EMantlePlaceImportStepState State = EMantlePlaceImportStepState::Waiting;
	FString Reason;
	bool bStarted = false;
	double StartedAt = 0.0;
	double EndedAt = 0.0;
};

class FMantlePlaceImportSteps
{
public:
	TFunction<void()> OnChanged;

	int32 Add(FName Id, const FString& Name);

	int32 Find(FName Id) const;

	const TArray<FMantlePlaceImportStep>& Steps() const { return StepList; }

	void Skip(FName Id, const FString& Reason);

	bool Begin(FName Id, double Now);

	void Pause(FName Id, double Now);

	void End(FName Id, bool bSucceeded, double Now);

	void SetActivity(const FString& InActivity);

	void RequestCancel();
	bool IsCancelRequested() const { return bCancelRequested; }

	void Finish(double Now);
	bool IsFinished() const { return bFinished; }

	bool WasCancelled() const;

	int32 CurrentIndex() const;

	void SetSynchronous(bool bInSynchronous);
	bool IsSynchronous() const { return bSynchronous; }

	double RunStartedAt() const { return RunStarted; }
	double RunEndedAt() const { return RunEnded; }

	static FString Heading() { return TEXT("Bundle Import"); }
	static FString WindowTitle() { return TEXT("Mantle Place Bundle Import"); }
	static FString CancelWord() { return TEXT("Cancel"); }
	static FString ElapsedWord() { return TEXT("Elapsed"); }

	static FString StateWord(EMantlePlaceImportStepState State);

	static FString Elapsed(double Seconds);

	FString StatusLine(double Now) const;

	FString RunElapsed(double Now) const;

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
