// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

struct FMantlePlaceVaultItem;
class UMantlePlaceVaultPanelController;

DECLARE_DELEGATE_OneParam(FMantlePlaceOnDetailImport, TSharedPtr<FMantlePlaceVaultItem>);

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
