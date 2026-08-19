// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

struct FMantlePlaceVaultItem;
class UMantlePlaceVaultPanelController;

/** Fired when the details page's Import button is clicked; carries the shown item. */
DECLARE_DELEGATE_OneParam(FMantlePlaceOnDetailImport, TSharedPtr<FMantlePlaceVaultItem> /*Item*/);

/**
 * The bundle details sub-page: a read-only, high-level view of one owned bundle (codename, status,
 * extent, size, tier, layers, per-format artifacts) plus Import. It mirrors the website's vault
 * detail (CuratorVaultDetailState) minus any advanced-processing / format-catalog surface - the
 * editor only imports; format generation lives on the web. The "List / Detail" breadcrumb (the back
 * affordance) is owned by the parent panel so it stays visible on both states. All presentation is
 * derived by the pure BuildVaultDetailView() so this widget just paints.
 */
class SMantlePlaceVaultDetail : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceVaultDetail) {}
		SLATE_ARGUMENT(TSharedPtr<FMantlePlaceVaultItem>, Item)
		SLATE_ARGUMENT(UMantlePlaceVaultPanelController*, Controller)
		SLATE_EVENT(FMantlePlaceOnDetailImport, OnImportClicked)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

private:
	FReply OnImportButtonClicked();

	TSharedPtr<FMantlePlaceVaultItem> Item;
	TWeakObjectPtr<UMantlePlaceVaultPanelController> Controller;
	FMantlePlaceOnDetailImport OnImportClicked;
};
