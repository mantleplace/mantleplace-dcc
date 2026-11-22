// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceAuthTypes.h"
#include "MantlePlaceVaultPanelController.h"
#include "UObject/StrongObjectPtr.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/Views/SListView.h"

struct FMantlePlaceVaultItem;
struct FMantlePlaceImportResult;
class ITableRow;
class STableViewBase;
class STextBlock;
class SEditableTextBox;
class SButton;
class SBox;
class SWidgetSwitcher;
class FMantlePlaceImportSteps;
class FMantlePlaceImportWindowHost;
class FMantlePlaceLevelChoice;
template <typename ItemType> class SComboBox;

class SMantlePlaceVaultPanel : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceVaultPanel) {}
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual ~SMantlePlaceVaultPanel() override;

private:
	void HandleVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void HandleImportPhase(const FString& Phase, const FString& Message, float Fraction);
	void HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result);
	void HandleAuthChanged(EMantlePlaceAuthState NewState);
	void HandleClientUpdateRequired(const FMantlePlaceClientUpdate& Update);
	void HandleImportSteps(TSharedRef<FMantlePlaceImportSteps> Steps);
	bool HandleOfferLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice);
	void HandleImportWindowCancel();

	TSharedRef<SWidget> BuildHeader();
	TSharedRef<SWidget> BuildBreadcrumb();
	TSharedRef<SWidget> BuildListPage();

	TSharedRef<ITableRow> OnGenerateRow(TSharedPtr<FMantlePlaceVaultItem> Item, const TSharedRef<STableViewBase>& OwnerTable);
	void HandleRowImportClicked(TSharedPtr<FMantlePlaceVaultItem> Item);
	void HandleRowReportProblemClicked(TSharedPtr<FMantlePlaceVaultItem> Item);
	void RebuildRows();

	void OnSearchChanged(const FText& NewText);
	void ApplyFilter();
	void UpdateEmptyState();

	void ShowList();
	void ShowDetail(TSharedPtr<FMantlePlaceVaultItem> Item);
	FReply OnBreadcrumbListClicked();

	FReply OnAuthButtonClicked();
	FText GetAuthButtonText() const;
	bool IsAuthButtonEnabled() const;
	FReply OnSendFeedbackClicked();
	FReply OnOpenInCuratorClicked();
	FReply OnRefreshClicked();
	void UpdateHeaderState();

	FReply OnBrowseClicked();
	FReply OnLocalImportClicked();
	EMantlePlaceImportMode SelectedMode() const;
	TSharedRef<SWidget> OnGenerateModeWidget(TSharedPtr<FString> InMode) const;

	void ShowToast(const FString& Message, bool bSuccess) const;

	TStrongObjectPtr<UMantlePlaceVaultPanelController> Controller;

	TArray<TSharedPtr<FMantlePlaceVaultItem>> AllItems;
	TArray<TSharedPtr<FMantlePlaceVaultItem>> Items;
	TSharedPtr<SListView<TSharedPtr<FMantlePlaceVaultItem>>> ListView;
	TSharedPtr<FMantlePlaceVaultItem> ActiveItemPtr;
	FString SearchQuery;
	bool bLocalImportInFlight = false;
	TSharedPtr<FMantlePlaceImportWindowHost> ImportWindow;
	bool bUpdatePromptShown = false;

	TSharedPtr<SWidgetSwitcher> ViewSwitcher;
	TSharedPtr<SBox> DetailHost;
	bool bShowingDetail = false;

	TSharedPtr<STextBlock> HeaderStatus;
	TSharedPtr<SEditableTextBox> SearchBox;
	TSharedPtr<SWidget> EmptyStateBox;
	TSharedPtr<STextBlock> EmptyEyebrow;
	TSharedPtr<STextBlock> EmptyHeading;
	TSharedPtr<STextBlock> EmptySub;
	TSharedPtr<SEditableTextBox> ZipPathText;
	TSharedPtr<STextBlock> LocalStatus;

	TArray<TSharedPtr<FString>> ModeOptions;
	TSharedPtr<FString> SelectedModeOption;
};
