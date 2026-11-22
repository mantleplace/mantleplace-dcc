// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;
struct FMantlePlaceLevelSize;

struct FMantlePlaceLevelOption
{
	FString Level;
	FString Label;
	FString ToolTip;
	bool bRecommended = false;
};

class FMantlePlaceLevelChoice
{
public:
	static TSharedPtr<FMantlePlaceLevelChoice> For(const FMantlePlaceVaultItem& Item);

	static FString Heading() { return TEXT("Fidelity Level"); }
	static FString RecommendedWord() { return TEXT("Recommended"); }
	static FString DownloadWord() { return TEXT("Download"); }
	static FString NoneRecommendedNote() { return TEXT("Nothing is recommended for this order. Choose a level to download."); }

	static FString OptionLabel(const FMantlePlaceLevelSize& Size, bool bRecommended);
	static FString OptionToolTip(const FMantlePlaceLevelSize& Size);

	const FString& OrderId() const { return Order; }
	const TArray<FMantlePlaceLevelOption>& Options() const { return LevelOptions; }
	const FString& Selected() const { return Selection; }
	bool HasSelection() const { return !Selection.IsEmpty(); }
	FString Note() const { return Recommended.IsEmpty() ? NoneRecommendedNote() : FString(); }

	bool Choose(const FString& Level);

	bool IsConfirmed() const { return bConfirmed; }
	bool Confirm();

	TFunction<void(const FString& Level)> OnConfirmed;

private:
	FString Order;
	FString Recommended;
	FString Selection;
	TArray<FMantlePlaceLevelOption> LevelOptions;
	bool bConfirmed = false;
};
