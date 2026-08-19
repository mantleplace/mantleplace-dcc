// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/Views/STableRow.h"
#include "Widgets/Views/STableViewBase.h"

struct FMantlePlaceVaultItem;
class UMantlePlaceVaultPanelController;

/** Fired when a row's import button is clicked, or the card body is clicked (open details); carries the item. */
DECLARE_DELEGATE_OneParam(FMantlePlaceOnRowImportClicked, TSharedPtr<FMantlePlaceVaultItem> /*Item*/);

/**
 * One vault-list bundle card. Native Slate replacement for the former UMantlePlaceVaultRowWidget: a
 * rounded/hairline "bento" card (MantlePlace.Card) that paints the pure BuildVaultRowView (codename,
 * meta, status pill, uniform Import) and drives a progress line for the bundle currently importing.
 * The whole card is clickable (opens the details page); the nested Import button consumes its own
 * click. The card is rebuilt whenever the list/busy state changes (SListView::RebuildList), so the
 * paint is computed once at Construct.
 */
class SMantlePlaceVaultRow : public STableRow<TSharedPtr<FMantlePlaceVaultItem>>
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceVaultRow) {}
		SLATE_ARGUMENT(TSharedPtr<FMantlePlaceVaultItem>, Item)
		SLATE_ARGUMENT(UMantlePlaceVaultPanelController*, Controller)
		SLATE_EVENT(FMantlePlaceOnRowImportClicked, OnImportClicked)
		SLATE_EVENT(FMantlePlaceOnRowImportClicked, OnSelected)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs, const TSharedRef<STableViewBase>& OwnerTable);

	/** Drive this row's progress line during its import (Fraction < 0 => indeterminate marquee). */
	void UpdateProgress(const FString& Phase, const FString& Message, float Fraction);

private:
	FReply OnPrimaryButtonClicked();
	FReply OnCardClicked();

	TSharedPtr<FMantlePlaceVaultItem> Item;
	TWeakObjectPtr<UMantlePlaceVaultPanelController> Controller;
	FMantlePlaceOnRowImportClicked OnImportClicked;
	FMantlePlaceOnRowImportClicked OnSelected;

	// Transient progress state, read by the progress widgets' attribute lambdas.
	bool bShowProgress = false;
	float ProgressFraction = -1.0f;
	FString ProgressLine;
};
