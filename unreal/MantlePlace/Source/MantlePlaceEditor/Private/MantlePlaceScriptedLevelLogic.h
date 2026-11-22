// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;

class FMantlePlaceScriptedLevelLogic
{
public:
	static FString Refusal(const FMantlePlaceVaultItem& Item, const FString& Level);
};
