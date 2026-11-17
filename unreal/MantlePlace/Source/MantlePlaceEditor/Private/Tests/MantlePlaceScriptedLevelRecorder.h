// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceImportTypes.h"

#include "MantlePlaceScriptedLevelRecorder.generated.h"

/**
 * What a scripted import tells its caller, caught for MantlePlace.Vault.ScriptedLevel: the orchestrator's
 * events are dynamic delegates, and a dynamic delegate binds a reflected function. Holds nothing the
 * engine reads; the test creates one, binds it, and reads what came through.
 */
UCLASS()
class UMantlePlaceScriptedLevelRecorder : public UObject
{
	GENERATED_BODY()

public:
	UFUNCTION()
	void HandlePhase(const FString& Phase, const FString& Message, float Fraction)
	{
		Phases.Add(Phase);
		Messages.Add(Message);
	}

	UFUNCTION()
	void HandleFinished(bool bSuccess, const FMantlePlaceImportResult& InResult)
	{
		++FinishedCount;
		bFinishedSuccess = bSuccess;
		Result = InResult;
	}

	TArray<FString> Phases;
	TArray<FString> Messages;
	int32 FinishedCount = 0;
	bool bFinishedSuccess = false;
	FMantlePlaceImportResult Result;
};
