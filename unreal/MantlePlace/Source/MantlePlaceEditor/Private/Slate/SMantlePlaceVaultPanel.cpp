// Copyright Mantle Place. All Rights Reserved.

#include "Slate/SMantlePlaceVaultPanel.h"

#include "Framework/Notifications/NotificationManager.h"
#include "MantlePlaceEditorStyle.h"
#include "MantlePlacePalette.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceVaultPanelController.h"
#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"
#include "Slate/SMantlePlaceVaultDetail.h"
#include "Slate/SMantlePlaceVaultRow.h"
#include "Styling/CoreStyle.h" // FCoreStyle "NoBorder" (breadcrumb link button)
#include "Styling/SlateTypes.h"
#include "UObject/Package.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SComboBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SSeparator.h"
#include "Widgets/Layout/SWidgetSwitcher.h"
#include "Widgets/Notifications/SNotificationList.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "MantlePlaceVaultPanel"

namespace
{
	using EFont = FMantlePlaceEditorStyle::EFont;

	/** Case-insensitive substring match of the search query against a bundle's codename (or order id). */
	bool ItemMatchesQuery(const FMantlePlaceVaultItem& Item, const FString& Query)
	{
		const FString Label = Item.AoiLabel.IsEmpty() ? Item.OrderId : Item.AoiLabel;
		return Label.Contains(Query); // FString::Contains is ESearchCase::IgnoreCase by default
	}
}

void SMantlePlaceVaultPanel::Construct(const FArguments& InArgs)
{
	// Own the controller (GC-rooted) which owns the orchestrator; bind its native events.
	Controller = TStrongObjectPtr<UMantlePlaceVaultPanelController>(
		NewObject<UMantlePlaceVaultPanelController>(GetTransientPackage()));
	Controller->Initialize();
	Controller->OnVaultListed.AddSP(this, &SMantlePlaceVaultPanel::HandleVaultListed);
	Controller->OnImportPhase.AddSP(this, &SMantlePlaceVaultPanel::HandleImportPhase);
	Controller->OnImportFinished.AddSP(this, &SMantlePlaceVaultPanel::HandleImportFinished);
	Controller->OnAuthChanged.AddSP(this, &SMantlePlaceVaultPanel::HandleAuthChanged);

	ModeOptions.Add(MakeShared<FString>(TEXT("Landscape")));
	ModeOptions.Add(MakeShared<FString>(TEXT("Mesh")));
	ModeOptions.Add(MakeShared<FString>(TEXT("Both")));
	SelectedModeOption = ModeOptions[0];

	// The details sub-page content is (re)built into this host on demand.
	DetailHost = SNew(SBox);

	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Panel.Background"))
		.Padding(0.f)
		[
			SNew(SVerticalBox)

			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				BuildHeader()
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				SNew(SSeparator).Thickness(1.f)
			]

			// "List / Detail" breadcrumb - always visible on both states, centered; the Detail segment is
			// greyed until a bundle is opened (see BuildBreadcrumb).
			+ SVerticalBox::Slot()
			.AutoHeight()
			.HAlign(HAlign_Center)
			.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, FMantlePlaceEditorStyle::S6)
			[
				BuildBreadcrumb()
			]

			+ SVerticalBox::Slot()
			.FillHeight(1.f)
			[
				SAssignNew(ViewSwitcher, SWidgetSwitcher)

				+ SWidgetSwitcher::Slot() // index 0: list
				[
					BuildListPage()
				]

				+ SWidgetSwitcher::Slot() // index 1: detail
				[
					DetailHost.ToSharedRef()
				]
			]
		]
	];

	ShowList();
	UpdateHeaderState();

	// A cached session (DPAPI token) means we can list straight away.
	if (Controller->IsSignedIn())
	{
		Controller->RefreshVaultList();
	}
}

SMantlePlaceVaultPanel::~SMantlePlaceVaultPanel()
{
	if (Controller.IsValid())
	{
		Controller->Shutdown();
	}
}

TSharedRef<SWidget> SMantlePlaceVaultPanel::BuildHeader()
{
	return SNew(SHorizontalBox)

		// Brand lockup: circular logo + stacked "mantle / place" wordmark.
		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(FMantlePlaceEditorStyle::S2, 12.f, 0.f, 12.f)
		[
			SNew(SImage)
			.Image(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Logo"))
			.DesiredSizeOverride(FVector2D(32.f, 32.f))
		]

		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Text(LOCTEXT("WordmarkMantle", "mantle"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Wordmark))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Text(LOCTEXT("WordmarkPlace", "place"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Wordmark))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
		]

		// Thin divider + "Vault" title with the status subline beneath.
		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(12.f, 8.f, 12.f, 8.f)
		[
			SNew(SBox).WidthOverride(1.f).HeightOverride(26.f)
			[
				SNew(SSeparator).Orientation(Orient_Vertical).Thickness(1.f)
			]
		]

		+ SHorizontalBox::Slot()
		.FillWidth(1.f)
		.VAlign(VAlign_Center)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Text(LOCTEXT("VaultTitle", "Vault"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Title))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6, 0.f, 0.f)
			[
				SAssignNew(HeaderStatus, STextBlock)
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
			]
		]

		// Sign In / Sign Out toggle.
		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(0.f, 0.f, FMantlePlaceEditorStyle::S3, 0.f)
		[
			SNew(SButton)
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Secondary"))
			.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
			.IsEnabled(this, &SMantlePlaceVaultPanel::IsAuthButtonEnabled)
			.OnClicked(this, &SMantlePlaceVaultPanel::OnAuthButtonClicked)
			[
				SNew(STextBlock)
				.Text(this, &SMantlePlaceVaultPanel::GetAuthButtonText)
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
		]

		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(0.f, 0.f, FMantlePlaceEditorStyle::S2, 0.f)
		[
			SNew(SButton)
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Secondary"))
			.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
			.OnClicked(this, &SMantlePlaceVaultPanel::OnRefreshClicked)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("Refresh", "Refresh"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
		];
}

TSharedRef<SWidget> SMantlePlaceVaultPanel::BuildBreadcrumb()
{
	// A persistent "List · Detail" breadcrumb shown on both states (mirrors the web vault breadcrumb).
	// "List" is always the clickable back affordance; it reads white while the list is the current page
	// and muted while it's the way back. "Detail" is never a link here - it stays greyed on the list and
	// turns white once a bundle is open. Colours are bound to bShowingDetail so they update on navigation.
	return SNew(SHorizontalBox)

		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SButton)
			.ButtonStyle(&FCoreStyle::Get().GetWidgetStyle<FButtonStyle>("NoBorder"))
			.ContentPadding(FMargin(0.f, 2.f))
			.OnClicked(this, &SMantlePlaceVaultPanel::OnBreadcrumbListClicked)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("BreadcrumbList", "List"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
				.ColorAndOpacity_Lambda([this]()
				{
					return FSlateColor(bShowingDetail ? MantlePlacePalette::Subtle() : MantlePlacePalette::White());
				})
			]
		]

		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6.f, 0.f)
		[
			SNew(STextBlock)
			.Text(FText::FromString(FString::Printf(TEXT("%c"), (TCHAR)0x00B7 /* middot */)))
			.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
			.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
		]

		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("BreadcrumbDetail", "Detail"))
			.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
			.ColorAndOpacity_Lambda([this]()
			{
				return FSlateColor(bShowingDetail ? MantlePlacePalette::White() : MantlePlacePalette::Faint());
			})
		];
}

TSharedRef<SWidget> SMantlePlaceVaultPanel::BuildListPage()
{
	return SNew(SVerticalBox)

		// Search bar (client-side filter over the owned-bundle list; mirrors the web "Search your vault").
		+ SVerticalBox::Slot()
		.AutoHeight()
		.Padding(FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2, 0.f)
		[
			SAssignNew(SearchBox, SEditableTextBox)
			.HintText(LOCTEXT("SearchHint", "Search your vault"))
			.OnTextChanged(this, &SMantlePlaceVaultPanel::OnSearchChanged)
		]

		// Owned-bundle card list (fills), with a centered empty-state overlay.
		+ SVerticalBox::Slot()
		.FillHeight(1.f)
		.Padding(FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2)
		[
			SNew(SOverlay)

			+ SOverlay::Slot()
			[
				SAssignNew(ListView, SListView<TSharedPtr<FMantlePlaceVaultItem>>)
				// Transparent container background so the list region reads as the panel's onyx Void (the
				// editor default paints an opaque warm "Recessed" fill behind the rows). Style is owned by
				// the persistent editor style set, so the pointer stays valid for the widget's life.
				.ListViewStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FTableViewStyle>("MantlePlace.ListView"))
				.ListItemsSource(&Items)
				.OnGenerateRow(this, &SMantlePlaceVaultPanel::OnGenerateRow)
				.SelectionMode(ESelectionMode::None)
			]

			+ SOverlay::Slot()
			.HAlign(HAlign_Center)
			.VAlign(VAlign_Center)
			[
				SAssignNew(EmptyStateBox, SVerticalBox)
				.Visibility(EVisibility::Collapsed)

				// Eyebrow (text swaps to "NO MATCHES" when a search filters everything out).
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
				[
					SAssignNew(EmptyEyebrow, STextBlock)
					.Text(LOCTEXT("EmptyEyebrow", "EMPTY"))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::Sub()))
				]

				// Heading.
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
				[
					SAssignNew(EmptyHeading, STextBlock)
					.Text(LOCTEXT("EmptyHeading", "Your first bundle will live here"))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Heading))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
				]

				// Sub-line (editor-adapted: no globe CTA; point at Sign In / the local-.zip panel below).
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
				[
					SNew(SBox).MaxDesiredWidth(360.f)
					[
						SAssignNew(EmptySub, STextBlock)
						.Text(LOCTEXT("EmptySub", "Sign in and refresh to see your purchased bundles - or import a downloaded .zip below."))
						.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
						.ColorAndOpacity(FSlateColor(MantlePlacePalette::Sub()))
						.Justification(ETextJustify::Center)
						.AutoWrapText(true)
					]
				]
			]
		]

		+ SVerticalBox::Slot()
		.AutoHeight()
		[
			SNew(SSeparator).Thickness(1.f)
		]

		// Local-zip import panel.
		+ SVerticalBox::Slot()
		.AutoHeight()
		.Padding(FMantlePlaceEditorStyle::S2)
		[
			SNew(SVerticalBox)

			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				SNew(STextBlock)
				.Text(LOCTEXT("LocalImportHeading", "Import a local bundle (.zip)"))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Title))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.FillWidth(1.f)
				.VAlign(VAlign_Center)
				[
					SAssignNew(ZipPathText, SEditableTextBox)
					.HintText(LOCTEXT("ZipHint", "Path to a downloaded bundle .zip"))
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
				[
					SNew(SButton)
					.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Secondary"))
					.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
					.OnClicked(this, &SMantlePlaceVaultPanel::OnBrowseClicked)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("Browse", "Browse..."))
						.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
						.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
					]
				]
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				[
					SNew(SComboBox<TSharedPtr<FString>>)
					.OptionsSource(&ModeOptions)
					.OnGenerateWidget(this, &SMantlePlaceVaultPanel::OnGenerateModeWidget)
					.OnSelectionChanged_Lambda([this](TSharedPtr<FString> NewSel, ESelectInfo::Type)
					{
						if (NewSel.IsValid()) { SelectedModeOption = NewSel; }
					})
					.InitiallySelectedItem(SelectedModeOption)
					[
						SNew(STextBlock)
						.Text_Lambda([this]()
						{
							return FText::FromString(SelectedModeOption.IsValid() ? *SelectedModeOption : TEXT("Landscape"));
						})
						.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
					]
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
				[
					SNew(SButton)
					.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Primary"))
					.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
					.IsEnabled_Lambda([this]() { return !(Controller.IsValid() && Controller->IsBusy()); })
					.OnClicked(this, &SMantlePlaceVaultPanel::OnLocalImportClicked)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("LocalImport", "Import"))
						.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
						.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
					]
				]
			]

			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
			[
				SAssignNew(LocalStatus, STextBlock)
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
				.AutoWrapText(true)
			]
		];
}

void SMantlePlaceVaultPanel::HandleVaultListed(bool /*bSuccess*/, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	// Only succeeded (Available) bundles are shown - failed / refunded / expired / still-processing
	// bundles are handled on the website, not in-editor.
	AllItems.Reset();
	for (const FMantlePlaceVaultItem& Bundle : Bundles)
	{
		if (Bundle.Status == EMantlePlaceVaultBundleStatus::Available)
		{
			AllItems.Add(MakeShared<FMantlePlaceVaultItem>(Bundle));
		}
	}
	// The item shared-ptrs were replaced, so the prior active pointer is stale.
	ActiveItemPtr.Reset();

	ApplyFilter(); // derive the displayed Items from the search box + refresh the list + empty state

	if (HeaderStatus.IsValid() && !Message.IsEmpty())
	{
		HeaderStatus->SetText(FText::FromString(Message));
	}
}

void SMantlePlaceVaultPanel::OnSearchChanged(const FText& NewText)
{
	SearchQuery = NewText.ToString();
	ApplyFilter();
}

void SMantlePlaceVaultPanel::ApplyFilter()
{
	const FString Query = SearchQuery.TrimStartAndEnd();

	Items.Reset();
	for (const TSharedPtr<FMantlePlaceVaultItem>& Item : AllItems)
	{
		if (Item.IsValid() && (Query.IsEmpty() || ItemMatchesQuery(*Item, Query)))
		{
			Items.Add(Item);
		}
	}

	if (ListView.IsValid())
	{
		ListView->RequestListRefresh();
	}
	UpdateEmptyState();
}

void SMantlePlaceVaultPanel::UpdateEmptyState()
{
	const bool bNoBundles = AllItems.Num() == 0;
	const bool bNoMatches = !bNoBundles && Items.Num() == 0;

	if (EmptyStateBox.IsValid())
	{
		EmptyStateBox->SetVisibility((bNoBundles || bNoMatches) ? EVisibility::Visible : EVisibility::Collapsed);
	}

	// Swap the empty-state copy: a genuinely empty vault vs. a search that matched nothing.
	if (bNoMatches)
	{
		if (EmptyEyebrow.IsValid()) { EmptyEyebrow->SetText(LOCTEXT("NoMatchEyebrow", "NO MATCHES")); }
		if (EmptyHeading.IsValid()) { EmptyHeading->SetText(LOCTEXT("NoMatchHeading", "No bundles match your search")); }
		if (EmptySub.IsValid())     { EmptySub->SetText(LOCTEXT("NoMatchSub", "Clear the search to see all your bundles.")); }
	}
	else
	{
		if (EmptyEyebrow.IsValid()) { EmptyEyebrow->SetText(LOCTEXT("EmptyEyebrow", "EMPTY")); }
		if (EmptyHeading.IsValid()) { EmptyHeading->SetText(LOCTEXT("EmptyHeading", "Your first bundle will live here")); }
		if (EmptySub.IsValid())     { EmptySub->SetText(LOCTEXT("EmptySub", "Sign in and refresh to see your purchased bundles - or import a downloaded .zip below.")); }
	}
}

void SMantlePlaceVaultPanel::HandleImportPhase(const FString& Phase, const FString& Message, float Fraction)
{
	if (ActiveItemPtr.IsValid() && ListView.IsValid())
	{
		if (TSharedPtr<ITableRow> Row = ListView->WidgetFromItem(ActiveItemPtr))
		{
			StaticCastSharedPtr<SMantlePlaceVaultRow>(Row)->UpdateProgress(Phase, Message, Fraction);
		}
	}
	// A local-zip import has no list row - show its progress in the local section instead.
	if (bLocalImportInFlight && LocalStatus.IsValid())
	{
		LocalStatus->SetText(FText::FromString(
			Message.IsEmpty() ? Phase : FString::Printf(TEXT("%s: %s"), *Phase, *Message)));
	}
	if (HeaderStatus.IsValid())
	{
		HeaderStatus->SetText(FText::FromString(
			Message.IsEmpty() ? Phase : FString::Printf(TEXT("%s: %s"), *Phase, *Message)));
	}
}

void SMantlePlaceVaultPanel::HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result)
{
	if (HeaderStatus.IsValid())
	{
		HeaderStatus->SetText(FText::FromString(Result.Message));
	}
	if (bLocalImportInFlight && LocalStatus.IsValid())
	{
		LocalStatus->SetText(FText::FromString(Result.Message));
	}
	bLocalImportInFlight = false;
	ActiveItemPtr.Reset();
	// Repaint rows enabled now the import is done. The controller also kicks a re-list, which will
	// rebuild the source with fresh integrity facts (Base -> Unreal) shortly after.
	RebuildRows();

	// Toast the outcome so the user is notified whether they're watching the panel or not.
	ShowToast(Result.Message.IsEmpty()
		? (bSuccess ? TEXT("Import complete.") : TEXT("Import failed."))
		: Result.Message, bSuccess);
}

void SMantlePlaceVaultPanel::HandleAuthChanged(EMantlePlaceAuthState NewState)
{
	UpdateHeaderState();

	if (NewState == EMantlePlaceAuthState::Authenticated)
	{
		// Sign-in completed (async browser round-trip) -> auto-load the vault, no manual Refresh.
		if (Controller.IsValid())
		{
			Controller->RefreshVaultList();
		}
	}
	else if (NewState == EMantlePlaceAuthState::Unauthenticated)
	{
		// Signed out -> clear the list and return to it.
		AllItems.Reset();
		Items.Reset();
		if (ListView.IsValid())
		{
			ListView->RequestListRefresh();
		}
		UpdateEmptyState();
		ShowList();
	}
}

TSharedRef<ITableRow> SMantlePlaceVaultPanel::OnGenerateRow(TSharedPtr<FMantlePlaceVaultItem> Item, const TSharedRef<STableViewBase>& OwnerTable)
{
	return SNew(SMantlePlaceVaultRow, OwnerTable)
		.Item(Item)
		.Controller(Controller.Get())
		.OnImportClicked(this, &SMantlePlaceVaultPanel::HandleRowImportClicked)
		.OnSelected(this, &SMantlePlaceVaultPanel::ShowDetail);
}

void SMantlePlaceVaultPanel::HandleRowImportClicked(TSharedPtr<FMantlePlaceVaultItem> Item)
{
	if (Controller.IsValid() && Item.IsValid() && Controller->StartImport(*Item, SelectedMode()))
	{
		ActiveItemPtr = Item;
		// Re-run every row's paint so bBusy disables the other rows.
		RebuildRows();

		// Pending toast so the user knows the (possibly long) generate+import sub-process began.
		const FString Codename = BuildVaultRowView(*Item, /*bBusy*/ true).Codename;
		FNotificationInfo Info(FText::FromString(FString::Printf(TEXT("Generating & importing %s..."), *Codename)));
		Info.ExpireDuration = 3.0f;
		Info.bFireAndForget = true;
		if (TSharedPtr<SNotificationItem> N = FSlateNotificationManager::Get().AddNotification(Info))
		{
			N->SetCompletionState(SNotificationItem::CS_Pending);
		}
	}
}

void SMantlePlaceVaultPanel::RebuildRows()
{
	if (ListView.IsValid())
	{
		ListView->RebuildList();
	}
}

void SMantlePlaceVaultPanel::ShowList()
{
	bShowingDetail = false;
	if (ViewSwitcher.IsValid())
	{
		ViewSwitcher->SetActiveWidgetIndex(0);
	}
}

void SMantlePlaceVaultPanel::ShowDetail(TSharedPtr<FMantlePlaceVaultItem> Item)
{
	if (!Item.IsValid() || !DetailHost.IsValid() || !ViewSwitcher.IsValid())
	{
		return;
	}

	DetailHost->SetContent(
		SNew(SMantlePlaceVaultDetail)
		.Item(Item)
		.Controller(Controller.Get())
		.OnImportClicked(FMantlePlaceOnDetailImport::CreateLambda(
			[this](TSharedPtr<FMantlePlaceVaultItem> ImportItem)
			{
				// Return to the list (where the row shows progress) then start the import.
				ShowList();
				HandleRowImportClicked(ImportItem);
			})));

	bShowingDetail = true;
	ViewSwitcher->SetActiveWidgetIndex(1);
}

FReply SMantlePlaceVaultPanel::OnBreadcrumbListClicked()
{
	// "List" is the back affordance; on the list state this is a harmless no-op.
	ShowList();
	return FReply::Handled();
}

FReply SMantlePlaceVaultPanel::OnAuthButtonClicked()
{
	if (Controller.IsValid())
	{
		if (Controller->IsSignedIn())
		{
			Controller->SignOut();
		}
		else
		{
			Controller->SignIn();
		}
	}
	UpdateHeaderState();
	return FReply::Handled();
}

FText SMantlePlaceVaultPanel::GetAuthButtonText() const
{
	if (Controller.IsValid())
	{
		switch (Controller->GetAuthState())
		{
		case EMantlePlaceAuthState::Authenticated:
			return LOCTEXT("SignOut", "Sign Out");
		case EMantlePlaceAuthState::Authenticating:
		case EMantlePlaceAuthState::Refreshing:
			return LOCTEXT("SigningIn", "Signing in...");
		default:
			break;
		}
	}
	return LOCTEXT("SignIn", "Sign In");
}

bool SMantlePlaceVaultPanel::IsAuthButtonEnabled() const
{
	if (!Controller.IsValid())
	{
		return true;
	}
	const EMantlePlaceAuthState State = Controller->GetAuthState();
	return State != EMantlePlaceAuthState::Authenticating && State != EMantlePlaceAuthState::Refreshing;
}

FReply SMantlePlaceVaultPanel::OnRefreshClicked()
{
	if (Controller.IsValid())
	{
		Controller->RefreshVaultList();
	}
	return FReply::Handled();
}

void SMantlePlaceVaultPanel::UpdateHeaderState()
{
	const bool bSignedIn = Controller.IsValid() && Controller->IsSignedIn();
	if (HeaderStatus.IsValid())
	{
		HeaderStatus->SetText(bSignedIn
			? LOCTEXT("SignedIn", "Signed in.")
			: LOCTEXT("SignInPrompt", "Sign in to load your vault."));
	}
}

FReply SMantlePlaceVaultPanel::OnBrowseClicked()
{
	FString Path;
	if (UMantlePlaceImporterLibrary::BrowseForVaultZip(Path) && ZipPathText.IsValid())
	{
		ZipPathText->SetText(FText::FromString(Path));
	}
	return FReply::Handled();
}

FReply SMantlePlaceVaultPanel::OnLocalImportClicked()
{
	if (!ZipPathText.IsValid() || !Controller.IsValid())
	{
		return FReply::Handled();
	}
	if (Controller->IsBusy())
	{
		return FReply::Handled(); // only one import runs at a time
	}

	const FString Zip = ZipPathText->GetText().ToString().TrimStartAndEnd();
	if (Zip.IsEmpty())
	{
		if (LocalStatus.IsValid())
		{
			LocalStatus->SetText(LOCTEXT("LocalNoPath", "Choose a bundle .zip to import (use Browse...)."));
		}
		return FReply::Handled();
	}

	// Async now (the local path can trigger a cloud materialize for a bundle missing its Unreal formats):
	// progress flows through HandleImportPhase into LocalStatus, the result through HandleImportFinished.
	bLocalImportInFlight = true;
	if (LocalStatus.IsValid())
	{
		LocalStatus->SetText(LOCTEXT("LocalStarting", "Starting import..."));
	}
	RebuildRows(); // reflect the busy state on the vault rows too

	if (!Controller->StartLocalImport(Zip, SelectedMode()))
	{
		// Never started (e.g. already busy) - HandleImportFinished won't fire, so clear the flag here.
		bLocalImportInFlight = false;
	}
	return FReply::Handled();
}

EMantlePlaceImportMode SMantlePlaceVaultPanel::SelectedMode() const
{
	if (SelectedModeOption.IsValid())
	{
		if (*SelectedModeOption == TEXT("Mesh")) { return EMantlePlaceImportMode::Mesh; }
		if (*SelectedModeOption == TEXT("Both")) { return EMantlePlaceImportMode::Both; }
	}
	return EMantlePlaceImportMode::Landscape;
}

TSharedRef<SWidget> SMantlePlaceVaultPanel::OnGenerateModeWidget(TSharedPtr<FString> InMode) const
{
	return SNew(STextBlock)
		.Text(FText::FromString(InMode.IsValid() ? *InMode : FString()))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body));
}

void SMantlePlaceVaultPanel::ShowToast(const FString& Message, bool bSuccess) const
{
	FNotificationInfo Info(FText::FromString(Message));
	Info.ExpireDuration = 4.5f;
	Info.bFireAndForget = true;
	if (TSharedPtr<SNotificationItem> Notification = FSlateNotificationManager::Get().AddNotification(Info))
	{
		Notification->SetCompletionState(bSuccess ? SNotificationItem::CS_Success : SNotificationItem::CS_Fail);
	}
}

#undef LOCTEXT_NAMESPACE
