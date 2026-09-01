// Copyright Mantle Place. All Rights Reserved.

#include "Slate/SMantlePlaceVaultRow.h"

#include "MantlePlaceEditorStyle.h"
#include "MantlePlacePalette.h"
#include "MantlePlaceVaultPanelController.h"
#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"
#include "Styling/SlateTypes.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SNullWidget.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Notifications/SProgressBar.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "MantlePlaceVaultRow"

void SMantlePlaceVaultRow::Construct(const FArguments& InArgs, const TSharedRef<STableViewBase>& OwnerTable)
{
	Item = InArgs._Item;
	Controller = InArgs._Controller;
	OnImportClicked = InArgs._OnImportClicked;
	OnSelected = InArgs._OnSelected;

	const bool bBusy = Controller.IsValid() && Controller->IsBusy();
	const FMantlePlaceVaultRowView View = Item.IsValid()
		? BuildVaultRowView(*Item, bBusy)
		: FMantlePlaceVaultRowView();

	using EFont = FMantlePlaceEditorStyle::EFont;

	STableRow<TSharedPtr<FMantlePlaceVaultItem>>::Construct(
		STableRow<TSharedPtr<FMantlePlaceVaultItem>>::FArguments()
		.Style(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FTableRowStyle>("MantlePlace.TableRow"))
		.ShowSelection(false)
		.Padding(FMargin(0.f, 0.f, 0.f, FMantlePlaceEditorStyle::S2)) // inter-card gap == edge gutter
		[
			// The whole card is a clickable frame (opens details); the Import button below is a nested
			// button that consumes its own click, so it never triggers the card's OnClicked.
			SNew(SButton)
			// Every card carries the same tag; tag lookup returns the first
			// visible match, i.e. the top row — resolve-then-click tooling that
			// needs a specific bundle should search the vault first.
			.Tag(TEXT("MantlePlace.Vault.BundleCard"))
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.CardButton"))
			.ContentPadding(FMargin(FMantlePlaceEditorStyle::S2)) // card inner padding, symmetric 16
			.OnClicked(this, &SMantlePlaceVaultRow::OnCardClicked)
			[
				SNew(SVerticalBox)

				// Tier 1: codename + meta (left) / status word (top-right).
				+ SVerticalBox::Slot()
				.AutoHeight()
				[
					SNew(SHorizontalBox)

					+ SHorizontalBox::Slot()
					.FillWidth(1.f)
					.VAlign(VAlign_Center)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot()
						.AutoHeight()
						[
							SNew(STextBlock)
							.Text(FText::FromString(View.Codename))
							.Font(FMantlePlaceEditorStyle::GetFont(EFont::Heading))
							.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(0.f, FMantlePlaceEditorStyle::S6, 0.f, 0.f)
						[
							SNew(STextBlock)
							.Text(FText::FromString(View.MetaLine))
							.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
							.ColorAndOpacity(FSlateColor(MantlePlacePalette::Subtle()))
						]
					]

					// Status word: plain tinted text, no pill / no dot (web STATUS_LABEL_CLASS), vertically
					// centred against the identity block (web self-center).
					+ SHorizontalBox::Slot()
					.AutoWidth()
					.VAlign(VAlign_Center)
					.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
					[
						SNew(STextBlock)
						.Text(FText::FromString(View.StatusLabel))
						.ColorAndOpacity(FSlateColor(View.StatusColor))
						.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
					]
				]

				// Action row: Import on its own right-aligned line (web moves the CTA below the meta).
				+ SVerticalBox::Slot()
				.AutoHeight()
				.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f)[ SNullWidget::NullWidget ]
					+ SHorizontalBox::Slot()
					.AutoWidth()
					[
						SNew(SButton)
						.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Primary"))
						.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
						.IsEnabled(View.bPrimaryEnabled)
						.OnClicked(this, &SMantlePlaceVaultRow::OnPrimaryButtonClicked)
						[
							SNew(STextBlock)
							.Text(FText::FromString(View.PrimaryLabel))
							.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
							.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
						]
					]
				]

				// Progress bar (hidden until UpdateProgress; marquee when Fraction < 0).
				+ SVerticalBox::Slot()
				.AutoHeight()
				.Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
				[
					SNew(SProgressBar)
					.FillColorAndOpacity(FSlateColor(MantlePlacePalette::Water()))
					.Visibility_Lambda([this]() { return bShowProgress ? EVisibility::HitTestInvisible : EVisibility::Collapsed; })
					.Percent_Lambda([this]() -> TOptional<float>
					{
						return (bShowProgress && ProgressFraction >= 0.f) ? TOptional<float>(ProgressFraction) : TOptional<float>();
					})
				]

				// Progress caption.
				+ SVerticalBox::Slot()
				.AutoHeight()
				.Padding(0.f, FMantlePlaceEditorStyle::S6, 0.f, 0.f)
				[
					SNew(STextBlock)
					.Visibility_Lambda([this]() { return bShowProgress ? EVisibility::HitTestInvisible : EVisibility::Collapsed; })
					.Text_Lambda([this]() { return FText::FromString(ProgressLine); })
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::Subtle()))
				]
			]
		],
		OwnerTable);
}

FReply SMantlePlaceVaultRow::OnPrimaryButtonClicked()
{
	OnImportClicked.ExecuteIfBound(Item);
	return FReply::Handled();
}

FReply SMantlePlaceVaultRow::OnCardClicked()
{
	OnSelected.ExecuteIfBound(Item);
	return FReply::Handled();
}

void SMantlePlaceVaultRow::UpdateProgress(const FString& Phase, const FString& Message, float Fraction)
{
	bShowProgress = true;
	ProgressFraction = Fraction;
	ProgressLine = Message.IsEmpty() ? Phase : Message;
	Invalidate(EInvalidateWidgetReason::Paint);
}

#undef LOCTEXT_NAMESPACE
