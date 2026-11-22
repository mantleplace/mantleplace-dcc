// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLevelChoiceLogic.h"

#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"

namespace
{
FString GroupedCount(int64 Value)
{
	const bool bNegative = Value < 0;
	FString Digits = FString::Printf(TEXT("%lld"), bNegative ? -Value : Value);
	FString Out;
	for (int32 i = 0; i < Digits.Len(); ++i)
	{
		if (i > 0 && (Digits.Len() - i) % 3 == 0)
		{
			Out.AppendChar(TEXT(','));
		}
		Out.AppendChar(Digits[i]);
	}
	return bNegative ? TEXT("-") + Out : Out;
}
}

TSharedPtr<FMantlePlaceLevelChoice> FMantlePlaceLevelChoice::For(const FMantlePlaceVaultItem& Item)
{
	if (!Item.bHasLevels || Item.Levels.Num() == 0)
	{
		return nullptr;
	}
	TSharedPtr<FMantlePlaceLevelChoice> Choice = MakeShared<FMantlePlaceLevelChoice>();
	Choice->Order = Item.OrderId;
	for (const FMantlePlaceLevelSize& Size : Item.Levels)
	{
		FMantlePlaceLevelOption Option;
		Option.Level = Size.Level;
		Option.bRecommended = !Item.RecommendedLevel.IsEmpty() && Size.Level.Equals(Item.RecommendedLevel, ESearchCase::CaseSensitive);
		Option.Label = OptionLabel(Size, Option.bRecommended);
		Option.ToolTip = OptionToolTip(Size);
		if (Option.bRecommended)
		{
			Choice->Recommended = Size.Level;
		}
		Choice->LevelOptions.Add(MoveTemp(Option));
	}
	Choice->Selection = Choice->Recommended;
	return Choice;
}

FString FMantlePlaceLevelChoice::OptionLabel(const FMantlePlaceLevelSize& Size, bool bRecommended)
{
	const FString Choice = FString::Printf(TEXT("%s — %s"), *Size.Level, *FormatVaultBytes(Size.SizeBytes));
	return bRecommended ? FString::Printf(TEXT("%s (%s)"), *Choice, *RecommendedWord()) : Choice;
}

FString FMantlePlaceLevelChoice::OptionToolTip(const FMantlePlaceLevelSize& Size)
{
	return FString::Printf(TEXT("%s: %s rows, %s features, %s triangles, %s pixels"), *Size.Level,
		*GroupedCount(Size.Rows), *GroupedCount(Size.Features), *GroupedCount(Size.Triangles), *GroupedCount(Size.Pixels));
}

bool FMantlePlaceLevelChoice::Choose(const FString& Level)
{
	if (bConfirmed)
	{
		return false;
	}
	const bool bOffered = LevelOptions.ContainsByPredicate(
		[&Level](const FMantlePlaceLevelOption& Option) { return Option.Level.Equals(Level, ESearchCase::CaseSensitive); });
	if (!bOffered)
	{
		return false;
	}
	Selection = Level;
	return true;
}

bool FMantlePlaceLevelChoice::Confirm()
{
	if (bConfirmed || Selection.IsEmpty())
	{
		return false;
	}
	bConfirmed = true;
	if (OnConfirmed)
	{
		OnConfirmed(Selection);
	}
	return true;
}
