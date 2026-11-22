// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class UWorld;

struct FMantlePlaceHandoverReport
{
	TArray<FString> Removed;
	TArray<FString> Swapped;
	TArray<FString> CreditActors;
	TArray<FString> Notes;

	FString Summary() const;
};

namespace MantlePlaceHandover
{
FMantlePlaceHandoverReport Prepare(UWorld* World);

FMantlePlaceHandoverReport PrepareEditorWorld();
}
