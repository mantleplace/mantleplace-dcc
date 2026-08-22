// Copyright Mantle Place. All Rights Reserved.

#include "Slate/SMantlePlaceVaultDetail.h"

#include "MantlePlaceEditorStyle.h"
#include "MantlePlacePalette.h"
#include "MantlePlaceVaultPanelController.h"
#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"
#include "Styling/SlateTypes.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SNullWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "MantlePlaceVaultDetail"

namespace
{
	using EFont = FMantlePlaceEditorStyle::EFont;

	/** A "{label}   {value}" definition row (mono label / body value), as on the web detail list. */
	TSharedRef<SWidget> MakeDefRow(const FString& Label, const FString& Value)
	{
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			[
				SNew(STextBlock)
				.Text(FText::FromString(Label.ToUpper()))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::Subtle()))
			]
			+ SHorizontalBox::Slot()
			.FillWidth(1.f)
			.HAlign(HAlign_Right)
			.VAlign(VAlign_Center)
			.Padding(FMantlePlaceEditorStyle::S3, 0.f, 0.f, 0.f)
			[
				SNew(STextBlock)
				.Text(FText::FromString(Value))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::Strong()))
				.Justification(ETextJustify::Right)
			];
	}
}

void SMantlePlaceVaultDetail::Construct(const FArguments& InArgs)
{
	Item = InArgs._Item;
	Controller = InArgs._Controller;
	OnImportClicked = InArgs._OnImportClicked;

	const bool bBusy = Controller.IsValid() && Controller->IsBusy();
	const FMantlePlaceVaultDetailView View = Item.IsValid()
		? BuildVaultDetailView(*Item, bBusy)
		: FMantlePlaceVaultDetailView();

	// Layers summary line.
	FString LayersText;
	if (View.bLayersKnown)
	{
		TArray<FString> Present;
		if (View.bImagery)   { Present.Add(TEXT("imagery")); }
		if (View.bBasemap)   { Present.Add(TEXT("basemap")); }
		if (View.bElevation) { Present.Add(TEXT("elevation")); }
		LayersText = Present.Num() > 0 ? FString::Join(Present, TEXT(", ")) : TEXT("none");
	}
	else
	{
		LayersText = TEXT("-");
	}

	// The merged "details" frame (identity + status + definition list).
	const TSharedRef<SVerticalBox> DetailsBox = SNew(SVerticalBox);

	DetailsBox->AddSlot().AutoHeight()
	[
		SNew(STextBlock)
		.Text(FText::FromString(View.Codename))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Heading))
		.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
	];
	if (!View.OrderLine.IsEmpty())
	{
		DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6, 0.f, 0.f)
		[
			SNew(STextBlock)
			.Text(FText::FromString(View.OrderLine))
			.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
			.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
		];
	}
	// Status word on its own line: plain tinted text, no pill / no dot.
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
	[
		SNew(STextBlock)
		.Text(FText::FromString(View.StatusLabel))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
		.ColorAndOpacity(FSlateColor(View.StatusColor))
	];
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
	[
		SNew(STextBlock)
		.Text(FText::FromString(View.MetaLine))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
		.ColorAndOpacity(FSlateColor(MantlePlacePalette::Subtle()))
	];

	// Definition list - set off from the meta above by gap spacing, not a hairline (web dropped divide-y).
	// Order mirrors the web detail: Extent, Delivered as, Total size, then tier/manifest/layers.
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S2, 0.f, FMantlePlaceEditorStyle::S6)[ MakeDefRow(TEXT("extent"), View.ExtentText) ];
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6)[ MakeDefRow(TEXT("delivered as"), View.DeliveredAsText) ];
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6)[ MakeDefRow(TEXT("total size"), View.SizeText) ];
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6)[ MakeDefRow(TEXT("format tier"), View.TierLabel) ];
	if (View.bHasManifestVersion)
	{
		DetailsBox->AddSlot().AutoHeight().Padding(0.f, 4.f)
		[ MakeDefRow(TEXT("manifest"), MantlePlaceDescribeManifestVersion(View.ManifestVersion)) ];
	}
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6)[ MakeDefRow(TEXT("layers"), LayersText) ];

	// Import action row.
	DetailsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S2, 0.f, 0.f)
	[
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f)[ SNullWidget::NullWidget ]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			SNew(SButton)
			.ButtonStyle(&FMantlePlaceEditorStyle::Get().GetWidgetStyle<FButtonStyle>("MantlePlace.Button.Primary"))
			.ContentPadding(FMantlePlaceEditorStyle::ButtonPaddingMd)
			.IsEnabled(View.bPrimaryEnabled)
			.OnClicked(this, &SMantlePlaceVaultDetail::OnImportButtonClicked)
			[
				SNew(STextBlock)
				.Text(FText::FromString(View.PrimaryLabel))
				.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
				.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
			]
		]
	];

	// The artifacts frame (per-format list), only when we know the artifacts.
	const TSharedRef<SVerticalBox> ArtifactsBox = SNew(SVerticalBox);
	ArtifactsBox->AddSlot().AutoHeight()
	[
		SNew(STextBlock)
		.Text(LOCTEXT("Artifacts", "ARTIFACTS"))
		.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
		.ColorAndOpacity(FSlateColor(MantlePlacePalette::Sub()))
	];
	if (View.Artifacts.Num() == 0)
	{
		ArtifactsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("NoArtifacts", "No per-format details recorded for this bundle."))
			.Font(FMantlePlaceEditorStyle::GetFont(EFont::Small))
			.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
		];
	}
	else
	{
		// Info-only per-format rows (the editor imports, it doesn't download raw artifacts): a dotted
		// label + a mono "{size} · opens with {tools}" sub-line, mirroring the web artifacts box.
		for (const FMantlePlaceVaultArtifactView& Artifact : View.Artifacts)
		{
			ArtifactsBox->AddSlot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S3, 0.f, 0.f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Text(FText::FromString(Artifact.Label))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Body))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::White()))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S6, 0.f, 0.f)
				[
					SNew(STextBlock)
					.Text(FText::FromString(Artifact.SubLine))
					.Font(FMantlePlaceEditorStyle::GetFont(EFont::Mono))
					.ColorAndOpacity(FSlateColor(MantlePlacePalette::Faint()))
					.AutoWrapText(true)
				]
			];
		}
	}

	// Paint the same onyx panel backdrop the list page draws so both switcher states share one palette
	// (the scroll body would otherwise fall back to the editor's default grey).
	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Panel.Background"))
		.Padding(0.f)
		[
			SNew(SScrollBox)
			+ SScrollBox::Slot()
			.Padding(FMantlePlaceEditorStyle::S2) // frames sit one gutter from every edge
			[
				SNew(SVerticalBox)

				// Details frame.
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SBorder)
					.BorderImage(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Frame"))
					.Padding(FMargin(FMantlePlaceEditorStyle::S2))
					[ DetailsBox ]
				]

				// Artifacts frame (inter-box gap == edge gutter).
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, FMantlePlaceEditorStyle::S2, 0.f, 0.f)
				[
					SNew(SBorder)
					.BorderImage(FMantlePlaceEditorStyle::Get().GetBrush("MantlePlace.Frame"))
					.Padding(FMargin(FMantlePlaceEditorStyle::S2))
					[ ArtifactsBox ]
				]
			]
		]
	];
}

FReply SMantlePlaceVaultDetail::OnImportButtonClicked()
{
	OnImportClicked.ExecuteIfBound(Item);
	return FReply::Handled();
}

#undef LOCTEXT_NAMESPACE
