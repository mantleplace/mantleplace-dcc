// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Slate/SMantlePlaceVaultPanel.h"

#include "Editor.h"
#include "Framework/Notifications/NotificationManager.h"
#include "HAL/PlatformProcess.h"
#include "Interfaces/IPluginManager.h"
#include "MantlePlaceCuratorExtentLogic.h"
#include "MantlePlaceEditorStyle.h"
#include "MantlePlaceExtentBox.h"
#include "MantlePlaceFeedbackLogic.h"
#include "MantlePlaceLevelChoiceLogic.h"
#include "MantlePlaceLevelExtent.h"
#include "MantlePlacePalette.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceVaultPanelController.h"
#include "MantlePlaceVaultClient.h"
#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/EngineVersion.h"
#include "Slate/SMantlePlaceImportWindow.h"
#include "Slate/SMantlePlaceVaultDetail.h"
#include "Slate/SMantlePlaceVaultRow.h"
#include "Styling/CoreStyle.h"
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

	bool ItemMatchesQuery(const FMantlePlaceVaultItem& Item, const FString& Query)
	{
		const FString Label = Item.AoiLabel.IsEmpty() ? Item.OrderId : Item.AoiLabel;
		return Label.Contains(Query);
	}

	void OpenFeedbackLink(const FString& OrderId, const FString& Rating = FString())
	{
		const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
		const FString PluginVersion = Plugin.IsValid() ? Plugin->GetDescriptor().VersionName : FString();
		const FEngineVersion& Engine = FEngineVersion::Current();
		const FString HostVersion = FString::Printf(TEXT("%u.%u"), Engine.GetMajor(), Engine.GetMinor());

		const FString Url = FMantlePlaceFeedbackLogic::BuildFeedbackUrl(
			GetDefault<UMantlePlaceVaultClient>()->VaultApiBaseUrl, PluginVersion, HostVersion, OrderId, Rating);
		if (!Url.IsEmpty())
		{
			FPlatformProcess::LaunchURL(*Url, nullptr, nullptr);
		}
	}

	void OpenCuratorLink(const FMantlePlaceResolvedExtent& Resolved)
	{
		const FString Url = MantlePlaceLevelExtent::CuratorUrl(Resolved);
		if (!Url.IsEmpty())
		{
			FPlatformProcess::LaunchURL(*Url, nullptr, nullptr);
		}
	}

	void ShowNoExtentPrompt(UWorld* World)
	{
		const bool bHasBox = MantlePlaceLevelExtent::ExtentBoxes(World).Num() > 0;
		const TSharedRef<TWeakPtr<SNotificationItem>> Shown = MakeShared<TWeakPtr<SNotificationItem>>();
		const auto Close = [Shown]()
		{
			if (const TSharedPtr<SNotificationItem> Notification = Shown->Pin())
			{
				Notification->ExpireAndFadeout();
			}
		};
		const FSimpleDelegate PlaceBox = FSimpleDelegate::CreateLambda([World = TWeakObjectPtr<UWorld>(World), Close]()
		{
			Close();
			if (MantlePlaceLevelExtent::PlaceExtentBox(World.Get()) != nullptr)
			{
				FNotificationInfo Info(LOCTEXT("ExtentBoxPlaced", "Extent box ready"));
				Info.SubText = LOCTEXT("ExtentBoxPlacedSub", "Set its Center Longitude and Center Latitude in Details, scale it over your site, then press Open in curator again.");
				Info.ExpireDuration = 9.f;
				Info.bFireAndForget = true;
				FSlateNotificationManager::Get().AddNotification(Info);
			}
		});
		const FSimpleDelegate OpenPlain = FSimpleDelegate::CreateLambda([Close]()
		{
			Close();
			OpenCuratorLink(FMantlePlaceResolvedExtent());
		});
		const FSimpleDelegate Dismiss = FSimpleDelegate::CreateLambda([Close]() { Close(); });

		FNotificationInfo Info(LOCTEXT("NoExtentTitle", "This level has no extent to open the curator on"));
		Info.SubText = bHasBox
			? LOCTEXT("NoExtentSubBox", "The Extent box is not located yet. Select it and set its Center Longitude and Center Latitude, or open the curator and draw the extent there.")
			: LOCTEXT("NoExtentSub", "It has no Mantle Place import, georeference or Extent box. Place an Extent box over your site, or open the curator and draw the extent there.");
		Info.bFireAndForget = false;
		Info.bUseSuccessFailIcons = false;
		Info.ButtonDetails.Add(FNotificationButtonInfo(
			bHasBox ? LOCTEXT("NoExtentSelectBox", "Select Extent box") : LOCTEXT("NoExtentPlaceBox", "Place Extent box"),
			FText::GetEmpty(), PlaceBox, SNotificationItem::CS_None));
		Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("NoExtentOpenPlain", "Open the curator"), FText::GetEmpty(), OpenPlain, SNotificationItem::CS_None));
		Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("NoExtentDismiss", "Dismiss"), FText::GetEmpty(), Dismiss, SNotificationItem::CS_None));
		*Shown = FSlateNotificationManager::Get().AddNotification(Info);
	}

	const TCHAR* const AskedSection = TEXT("MantlePlace.Feedback");
	const TCHAR* const AskedKey = TEXT("AskedOrder");

	void ShowImportFeedbackPrompt(const FString& OrderId)
	{
		const TSharedRef<TWeakPtr<SNotificationItem>> Shown = MakeShared<TWeakPtr<SNotificationItem>>();
		const auto Close = [Shown]()
		{
			if (const TSharedPtr<SNotificationItem> Notification = Shown->Pin())
			{
				Notification->ExpireAndFadeout();
			}
		};
		const auto Answer = [OrderId, Close](const FString& Rating)
		{
			return FSimpleDelegate::CreateLambda([OrderId, Rating, Close]()
			{
				if (!Rating.IsEmpty())
				{
					OpenFeedbackLink(OrderId, Rating);
				}
				Close();
			});
		};

		FNotificationInfo Info(LOCTEXT("ImportFeedbackQuestion", "Did this import come in right?"));
		Info.bFireAndForget = true;
		Info.ExpireDuration = 30.0f;
		Info.FadeOutDuration = 0.5f;
		Info.bUseSuccessFailIcons = false;
		Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("ImportFeedbackYes", "Yes"), FText::GetEmpty(), Answer(TEXT("up")), SNotificationItem::CS_None));
		Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("ImportFeedbackNo", "No"), FText::GetEmpty(), Answer(TEXT("down")), SNotificationItem::CS_None));
		Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("ImportFeedbackDismiss", "Dismiss"), FText::GetEmpty(), Answer(FString()), SNotificationItem::CS_None));
		*Shown = FSlateNotificationManager::Get().AddNotification(Info);
	}

	void MaybeAskAboutImport(bool bImportSucceeded, const FString& OrderId)
	{
		if (GConfig == nullptr)
		{
			return;
		}
		TArray<FString> Asked;
		GConfig->GetArray(AskedSection, AskedKey, Asked, GEditorPerProjectIni);
		if (!FMantlePlaceFeedbackLogic::ShouldAskAboutImport(bImportSucceeded, OrderId, Asked))
		{
			return;
		}
		FMantlePlaceFeedbackLogic::RememberAsked(OrderId, Asked);
		GConfig->SetArray(AskedSection, AskedKey, Asked, GEditorPerProjectIni);
		GConfig->Flush(false, GEditorPerProjectIni);
		ShowImportFeedbackPrompt(OrderId);
	}
}

void SMantlePlaceVaultPanel::Construct(const FArguments& InArgs)
{
	Controller = TStrongObjectPtr<UMantlePlaceVaultPanelController>(
		NewObject<UMantlePlaceVaultPanelController>(GetTransientPackage()));
	Controller->Initialize();
	Controller->OnVaultListed.AddSP(this, &SMantlePlaceVaultPanel::HandleVaultListed);
	Controller->OnImportPhase.AddSP(this, &SMantlePlaceVaultPanel::HandleImportPhase);
	Controller->OnImportFinished.AddSP(this, &SMantlePlaceVaultPanel::HandleImportFinished);
	Controller->OnAuthChanged.AddSP(this, &SMantlePlaceVaultPanel::HandleAuthChanged);
	Controller->OnClientUpdateRequired.AddSP(this, &SMantlePlaceVaultPanel::HandleClientUpdateRequired);
	Controller->OnImportSteps.AddSP(this, &SMantlePlaceVaultPanel::HandleImportSteps);
	Controller->OnOfferLevelChoice.BindSP(this, &SMantlePlaceVaultPanel::HandleOfferLevelChoice);

	ModeOptions.Add(MakeShared<FString>(TEXT("Landscape")));
	ModeOptions.Add(MakeShared<FString>(TEXT("Mesh")));
	ModeOptions.Add(MakeShared<FString>(TEXT("Both")));
	SelectedModeOption = ModeOptions[0];

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

				+ SWidgetSwitcher::Slot()
				[
					BuildListPage()
				]

				+ SWidgetSwitcher::Slot()
				[
					DetailHost.ToSharedRef()
				]
			]
		]
	];

	ShowList();
	UpdateHeaderState();

	if (Controller->IsSignedIn())
	{
		Controller->RefreshVaultList();
	}
	else
	{
		Controller->TryRestoreSession();
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

		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(FMantlePlaceEditorStyle::S2, 12.f, FMantlePlaceEditorStyle::S3, 12.f)
		[
			SNew(SImage)
			.Image(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Mark"))
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

		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(0.f, 0.f, FMantlePlaceEditorStyle::S3, 0.f)
		[
			SNew(SButton)
			.Tag(TEXT("MantlePlace.Vault.SignIn"))
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
			.Tag(TEXT("MantlePlace.Vault.SendFeedback"))
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Secondary"))
			.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
			.ToolTipText(LOCTEXT("SendFeedbackTip", "Open the Mantle Place feedback form in your browser. No account needed."))
			.OnClicked(this, &SMantlePlaceVaultPanel::OnSendFeedbackClicked)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("SendFeedback", "Send Feedback"))
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
			.Tag(TEXT("MantlePlace.Vault.OpenInCurator"))
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Secondary"))
			.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
			.ToolTipText(LOCTEXT("OpenInCuratorTip", "Open the Mantle Place curator in your browser on this level's extent, drawn and ready. No account needed."))
			.OnClicked(this, &SMantlePlaceVaultPanel::OnOpenInCuratorClicked)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("OpenInCurator", "Open in curator"))
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
			.Tag(TEXT("MantlePlace.Vault.Refresh"))
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
			.Text(FText::FromString(FString::Printf(TEXT("%c"), (TCHAR)0x00B7)))
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

		+ SVerticalBox::Slot()
		.AutoHeight()
		.Padding(FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2, 0.f)
		[
			SAssignNew(SearchBox, SEditableTextBox)
			.HintText(LOCTEXT("SearchHint", "Search your vault"))
			.OnTextChanged(this, &SMantlePlaceVaultPanel::OnSearchChanged)
		]

		+ SVerticalBox::Slot()
		.FillHeight(1.f)
		.Padding(FMantlePlaceEditorStyle::S2, FMantlePlaceEditorStyle::S2)
		[
			SNew(SOverlay)

			+ SOverlay::Slot()
			[
				SAssignNew(ListView, SListView<TSharedPtr<FMantlePlaceVaultItem>>)
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

				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
				[
					SAssignNew(EmptyEyebrow, STextBlock)
					.Text(LOCTEXT("EmptyEyebrow", "EMPTY"))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::Sub()))
				]

				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
				[
					SAssignNew(EmptyHeading, STextBlock)
					.Text(LOCTEXT("EmptyHeading", "Your first bundle will live here"))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Heading))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
				]

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
					.Tag(TEXT("MantlePlace.Vault.LocalZipPath"))
					.HintText(LOCTEXT("ZipHint", "Path to a downloaded bundle .zip"))
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
				[
					SNew(SButton)
					.Tag(TEXT("MantlePlace.Vault.Browse"))
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
					.Tag(TEXT("MantlePlace.Vault.ImportMode"))
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
					.Tag(TEXT("MantlePlace.Vault.LocalImport"))
					.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Primary"))
					.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
					.IsEnabled_Lambda([this]() { return !(Controller.IsValid() && Controller->IsBusy()); })
					.OnClicked(this, &SMantlePlaceVaultPanel::OnLocalImportClicked)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("LocalImport", "Import Bundle"))
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

void SMantlePlaceVaultPanel::HandleVaultListed(bool, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	AllItems.Reset();
	for (const FMantlePlaceVaultItem& Bundle : Bundles)
	{
		if (Bundle.Status == EMantlePlaceVaultBundleStatus::Available)
		{
			AllItems.Add(MakeShared<FMantlePlaceVaultItem>(Bundle));
		}
	}
	ActiveItemPtr.Reset();

	ApplyFilter();

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
	const FString ImportedOrderId = ActiveItemPtr.IsValid() ? ActiveItemPtr->OrderId : FString();
	if (ImportWindow.IsValid())
	{
		ImportWindow->SetResult(Result.Message);
	}
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
	RebuildRows();

	ShowToast(Result.Message.IsEmpty()
		? (bSuccess ? TEXT("Import complete.") : TEXT("Import failed."))
		: Result.Message, bSuccess);

	MaybeAskAboutImport(bSuccess, ImportedOrderId);
}

void SMantlePlaceVaultPanel::HandleImportSteps(TSharedRef<FMantlePlaceImportSteps> Steps)
{
	if (ImportWindow.IsValid())
	{
		ImportWindow->Close();
	}
	ImportWindow = FMantlePlaceImportWindowHost::Open(
		Steps, FSimpleDelegate::CreateSP(this, &SMantlePlaceVaultPanel::HandleImportWindowCancel));
}

bool SMantlePlaceVaultPanel::HandleOfferLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice)
{
	return ImportWindow.IsValid() && ImportWindow->ShowLevelChoice(Choice);
}

void SMantlePlaceVaultPanel::HandleImportWindowCancel()
{
	if (Controller.IsValid())
	{
		Controller->CancelImport();
	}
}

void SMantlePlaceVaultPanel::HandleAuthChanged(EMantlePlaceAuthState NewState)
{
	UpdateHeaderState();

	if (NewState == EMantlePlaceAuthState::Authenticated)
	{
		if (Controller.IsValid())
		{
			Controller->RefreshVaultList();
		}
	}
	else if (NewState == EMantlePlaceAuthState::Unauthenticated)
	{
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
		.OnSelected(this, &SMantlePlaceVaultPanel::ShowDetail)
		.OnReportProblemClicked(this, &SMantlePlaceVaultPanel::HandleRowReportProblemClicked);
}

void SMantlePlaceVaultPanel::HandleRowReportProblemClicked(TSharedPtr<FMantlePlaceVaultItem> Item)
{
	if (Item.IsValid())
	{
		OpenFeedbackLink(Item->OrderId);
	}
}

void SMantlePlaceVaultPanel::HandleRowImportClicked(TSharedPtr<FMantlePlaceVaultItem> Item)
{
	if (Controller.IsValid() && Item.IsValid() && Controller->StartImport(*Item, SelectedMode()))
	{
		ActiveItemPtr = Item;
		RebuildRows();

		const FString Codename = BuildVaultRowView(*Item, true).Codename;
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
				ShowList();
				HandleRowImportClicked(ImportItem);
			})));

	bShowingDetail = true;
	ViewSwitcher->SetActiveWidgetIndex(1);
}

FReply SMantlePlaceVaultPanel::OnBreadcrumbListClicked()
{
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

FReply SMantlePlaceVaultPanel::OnSendFeedbackClicked()
{
	OpenFeedbackLink(FString());
	return FReply::Handled();
}

FReply SMantlePlaceVaultPanel::OnOpenInCuratorClicked()
{
	UWorld* World = MantlePlaceLevelExtent::EditorWorld();
	const FMantlePlaceResolvedExtent Resolved = MantlePlaceLevelExtent::Resolve(World);
	if (!Resolved.IsSet())
	{
		ShowNoExtentPrompt(World);
		return FReply::Handled();
	}
	OpenCuratorLink(Resolved);
	ShowToast(FString::Printf(TEXT("Opening the curator on the extent from %s."),
		*FMantlePlaceCuratorExtentLogic::DescribeSource(Resolved.Source)), true);
	return FReply::Handled();
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
	if (!HeaderStatus.IsValid())
	{
		return;
	}

	if (bSignedIn)
	{
		HeaderStatus->SetText(LOCTEXT("SignedIn", "Signed in."));
		return;
	}

	const FString Reason = Controller.IsValid() ? Controller->GetLastAuthError() : FString();
	HeaderStatus->SetText(Reason.IsEmpty()
		? LOCTEXT("SignInPrompt", "Sign in to load your vault.")
		: FText::FromString(Reason));
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
		return FReply::Handled();
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

	bLocalImportInFlight = true;
	if (LocalStatus.IsValid())
	{
		LocalStatus->SetText(LOCTEXT("LocalStarting", "Starting import..."));
	}
	RebuildRows();

	if (!Controller->StartLocalImport(Zip, SelectedMode()))
	{
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
	const FString Mode = InMode.IsValid() ? *InMode : FString();
	return SNew(STextBlock)
		.Tag(FName(*FString::Printf(TEXT("MantlePlace.Vault.ImportModeOption.%s"), *Mode)))
		.Text(FText::FromString(Mode))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body));
}

void SMantlePlaceVaultPanel::HandleClientUpdateRequired(const FMantlePlaceClientUpdate& Update)
{
	if (bUpdatePromptShown)
	{
		return;
	}
	bUpdatePromptShown = true;

	const FString DownloadUrl = Update.DownloadUrl;
	const TSharedRef<TWeakPtr<SNotificationItem>> Shown = MakeShared<TWeakPtr<SNotificationItem>>();
	const FSimpleDelegate Download = FSimpleDelegate::CreateLambda([DownloadUrl, Shown]()
	{
		FPlatformProcess::LaunchURL(*DownloadUrl, nullptr, nullptr);
		if (const TSharedPtr<SNotificationItem> Notification = Shown->Pin())
		{
			Notification->ExpireAndFadeout();
		}
	});
	const FSimpleDelegate Dismiss = FSimpleDelegate::CreateLambda([Shown]()
	{
		if (const TSharedPtr<SNotificationItem> Notification = Shown->Pin())
		{
			Notification->ExpireAndFadeout();
		}
	});

	FNotificationInfo Info(LOCTEXT("UpdateRequiredTitle", "Mantle Place needs an update"));
	Info.SubText = FText::FromString(Update.Message);
	Info.bFireAndForget = false;
	Info.bUseSuccessFailIcons = true;
	Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("UpdateRequiredDownload", "Download update"), FText::GetEmpty(), Download, SNotificationItem::CS_Fail));
	Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("UpdateRequiredDismiss", "Dismiss"), FText::GetEmpty(), Dismiss, SNotificationItem::CS_Fail));
	*Shown = FSlateNotificationManager::Get().AddNotification(Info);
	if (const TSharedPtr<SNotificationItem> Notification = Shown->Pin())
	{
		Notification->SetCompletionState(SNotificationItem::CS_Fail);
	}
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
