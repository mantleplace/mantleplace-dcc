// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/Views/STableRow.h"
#include "Widgets/Views/STableViewBase.h"

struct FMantlePlaceVaultItem;
class UMantlePlaceVaultPanelController;

DECLARE_DELEGATE_OneParam(FMantlePlaceOnRowImportClicked, TSharedPtr<FMantlePlaceVaultItem>);

class SMantlePlaceVaultRow : public STableRow<TSharedPtr<FMantlePlaceVaultItem>>
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceVaultRow) {}
		SLATE_ARGUMENT(TSharedPtr<FMantlePlaceVaultItem>, Item)
		SLATE_ARGUMENT(UMantlePlaceVaultPanelController*, Controller)
		SLATE_EVENT(FMantlePlaceOnRowImportClicked, OnImportClicked)
		SLATE_EVENT(FMantlePlaceOnRowImportClicked, OnSelected)
		SLATE_EVENT(FMantlePlaceOnRowImportClicked, OnReportProblemClicked)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs, const TSharedRef<STableViewBase>& OwnerTable);

	void UpdateProgress(const FString& Phase, const FString& Message, float Fraction);

private:
	FReply OnPrimaryButtonClicked();
	FReply OnCardClicked();
	FReply OnReportProblemClicked();

	TSharedPtr<FMantlePlaceVaultItem> Item;
	TWeakObjectPtr<UMantlePlaceVaultPanelController> Controller;
	FMantlePlaceOnRowImportClicked OnImportClicked;
	FMantlePlaceOnRowImportClicked OnSelected;
	FMantlePlaceOnRowImportClicked OnReportProblem;

	bool bShowProgress = false;
	float ProgressFraction = -1.0f;
	FString ProgressLine;
};
