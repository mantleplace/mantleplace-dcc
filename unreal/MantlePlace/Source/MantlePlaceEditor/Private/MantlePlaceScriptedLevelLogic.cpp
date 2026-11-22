// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceScriptedLevelLogic.h"

#include "MantlePlaceVaultTypes.h"

FString FMantlePlaceScriptedLevelLogic::Refusal(const FMantlePlaceVaultItem& Item, const FString& Level)
{
	if (Level.IsEmpty())
	{
		return TEXT("No level was named. StartVaultImport imports an MPB 3.0 order at the level the platform "
			"recommends; to name one, pass Max, Med or Min.");
	}

	if (!Item.bHasLevels || Item.Levels.Num() == 0)
	{
		return FString::Printf(TEXT("This order offers no levels (it is not an MPB 3.0 order), so the %s level "
			"cannot be downloaded. Import it with StartVaultImport."), *Level);
	}

	TArray<FString> Offered;
	for (const FMantlePlaceLevelSize& Size : Item.Levels)
	{
		if (Size.Level.Equals(Level, ESearchCase::CaseSensitive))
		{
			return FString();
		}
		Offered.Add(Size.Level);
	}
	return FString::Printf(TEXT("This order does not offer the %s level. It offers: %s."),
		*Level, *FString::Join(Offered, TEXT(", ")));
}
