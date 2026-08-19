// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceImportTypes.h" // EMantlePlaceImportMode
#include "MantlePlaceAuthTypes.h"   // EMantlePlaceAuthState (auth-button state)
#include "MantlePlaceVaultPanelController.h" // complete type: TStrongObjectPtr member below
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
template <typename ItemType> class SComboBox;

/**
 * The Mantle Place vault tooling panel, hosted in a dockable nomad tab under Window > Mantle Place.
 * Native Slate surface styled to the website: a branded header (logo + "mantle / place" wordmark,
 * Sign In / Sign Out toggle + Refresh), and an SWidgetSwitcher between the bundle-card list (with the
 * local-zip import section) and a bundle details sub-page. Only succeeded (Available) bundles are
 * shown; the sole per-bundle action is Import (a BASE bundle materializes-then-imports transparently,
 * with progress + toast). All logic is reused from the C++ orchestrator via a
 * UMantlePlaceVaultPanelController held by TStrongObjectPtr; the panel binds its native events.
 */
class SMantlePlaceVaultPanel : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SMantlePlaceVaultPanel) {}
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual ~SMantlePlaceVaultPanel() override;

private:
	//~ Controller events (bound via AddSP).
	void HandleVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void HandleImportPhase(const FString& Phase, const FString& Message, float Fraction);
	void HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result);
	void HandleAuthChanged(EMantlePlaceAuthState NewState);

	//~ Construction helpers.
	TSharedRef<SWidget> BuildHeader();
	TSharedRef<SWidget> BuildBreadcrumb();
	TSharedRef<SWidget> BuildListPage();

	//~ List.
	TSharedRef<ITableRow> OnGenerateRow(TSharedPtr<FMantlePlaceVaultItem> Item, const TSharedRef<STableViewBase>& OwnerTable);
	void HandleRowImportClicked(TSharedPtr<FMantlePlaceVaultItem> Item);
	void RebuildRows();

	//~ Search / filter (client-side, over AllItems).
	void OnSearchChanged(const FText& NewText);
	void ApplyFilter();
	void UpdateEmptyState();

	//~ Navigation (list <-> detail).
	void ShowList();
	void ShowDetail(TSharedPtr<FMantlePlaceVaultItem> Item);
	/** The breadcrumb "List" segment (always the back affordance). */
	FReply OnBreadcrumbListClicked();

	//~ Header (auth toggle + refresh).
	FReply OnAuthButtonClicked();
	FText GetAuthButtonText() const;
	bool IsAuthButtonEnabled() const;
	FReply OnRefreshClicked();
	void UpdateHeaderState();

	//~ Local-zip import panel.
	FReply OnBrowseClicked();
	FReply OnLocalImportClicked();
	EMantlePlaceImportMode SelectedMode() const;
	TSharedRef<SWidget> OnGenerateModeWidget(TSharedPtr<FString> InMode) const;

	//~ Editor toast helper (import start / success / failure).
	void ShowToast(const FString& Message, bool bSuccess) const;

	/** GC-root for the controller (which UPROPERTY-owns the orchestrator + its clients). */
	TStrongObjectPtr<UMantlePlaceVaultPanelController> Controller;

	/** Every Available bundle from the last list (the filter source of truth). */
	TArray<TSharedPtr<FMantlePlaceVaultItem>> AllItems;
	/** The currently displayed subset of AllItems (after the search filter). */
	TArray<TSharedPtr<FMantlePlaceVaultItem>> Items;
	TSharedPtr<SListView<TSharedPtr<FMantlePlaceVaultItem>>> ListView;
	/** The bundle currently importing (used to route progress to its row); reset when idle. */
	TSharedPtr<FMantlePlaceVaultItem> ActiveItemPtr;
	/** Current search text (case-insensitive substring over codename / order id). */
	FString SearchQuery;
	/** True while a local-zip import is running (routes progress to LocalStatus + disables local Import). */
	bool bLocalImportInFlight = false;

	//~ List <-> detail navigation.
	TSharedPtr<SWidgetSwitcher> ViewSwitcher;
	TSharedPtr<SBox> DetailHost;
	/** True while the detail sub-page is shown; drives the breadcrumb's active/greyed segment colours. */
	bool bShowingDetail = false;

	//~ Widgets updated imperatively.
	TSharedPtr<STextBlock> HeaderStatus;
	TSharedPtr<SEditableTextBox> SearchBox;
	TSharedPtr<SWidget> EmptyStateBox;
	TSharedPtr<STextBlock> EmptyEyebrow;
	TSharedPtr<STextBlock> EmptyHeading;
	TSharedPtr<STextBlock> EmptySub;
	TSharedPtr<SEditableTextBox> ZipPathText;
	TSharedPtr<STextBlock> LocalStatus;

	//~ Import-mode combo (Landscape / Mesh / Both).
	TArray<TSharedPtr<FString>> ModeOptions;
	TSharedPtr<FString> SelectedModeOption;
};
