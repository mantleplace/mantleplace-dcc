// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;
struct FMantlePlaceLevelSize;

/** One level the control offers: what the curator reads, and what its counts are. */
struct FMantlePlaceLevelOption
{
	/** The level as the download offers it (`Max`, `Med` or `Min`). */
	FString Level;
	/** The level, then its size in this host's size format, then `(Recommended)` on the recommended one. */
	FString Label;
	/** The counts behind the size, as the platform states them. */
	FString ToolTip;
	bool bRecommended = false;
};

/**
 * The level control of a 3.0 order (HPS-59): the one level a download takes, chosen in the import
 * window before the download starts. Pure state, so every word and every change is tested headless;
 * the import window lays it out and the orchestrator downloads what it confirms.
 *
 *  - **The levels and their sizes are the platform's** (the vault listing's offer), applied verbatim:
 *    this unit sums no file sizes and works out no recommendation (HPS-33).
 *  - **The recommendation is preselected and said;** told none, nothing is preselected and the control
 *    says nothing is recommended, so Download waits for the curator's choice.
 *  - **Download confirms once.** After it the level is fixed for this download.
 */
class FMantlePlaceLevelChoice
{
public:
	/** The control for a 3.0 order's offer; null for an order with no levels (a 2.x order downloads as it did). */
	static TSharedPtr<FMantlePlaceLevelChoice> For(const FMantlePlaceVaultItem& Item);

	/** The words (HPS-51's table, HPS-59). */
	static FString Heading() { return TEXT("Fidelity Level"); }
	static FString RecommendedWord() { return TEXT("Recommended"); }
	static FString DownloadWord() { return TEXT("Download"); }
	static FString NoneRecommendedNote() { return TEXT("Nothing is recommended for this order. Choose a level to download."); }

	/** `Med — 1.3 GB (Recommended)`. */
	static FString OptionLabel(const FMantlePlaceLevelSize& Size, bool bRecommended);
	/** `Med: 400 rows, 120 features, 2,000,000 triangles, 4,000,000 pixels`. */
	static FString OptionToolTip(const FMantlePlaceLevelSize& Size);

	const FString& OrderId() const { return Order; }
	const TArray<FMantlePlaceLevelOption>& Options() const { return LevelOptions; }
	/** The selected level, empty while none is. */
	const FString& Selected() const { return Selection; }
	bool HasSelection() const { return !Selection.IsEmpty(); }
	/** The line under the levels: the no-recommendation note, or empty. */
	FString Note() const { return Recommended.IsEmpty() ? NoneRecommendedNote() : FString(); }

	/** Select an offered level. False, changing nothing, for a level not offered or once confirmed. */
	bool Choose(const FString& Level);

	bool IsConfirmed() const { return bConfirmed; }
	/** Download: fires OnConfirmed with the selection, once. False with no selection or when already confirmed. */
	bool Confirm();

	/** Called once, by Confirm, with the level to download. */
	TFunction<void(const FString& Level)> OnConfirmed;

private:
	FString Order;
	FString Recommended;
	FString Selection;
	TArray<FMantlePlaceLevelOption> LevelOptions;
	bool bConfirmed = false;
};
