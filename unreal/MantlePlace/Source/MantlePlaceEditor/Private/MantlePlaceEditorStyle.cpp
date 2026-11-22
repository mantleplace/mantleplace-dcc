// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceEditorStyle.h"

#include "MantlePlacePalette.h"
#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Styling/CoreStyle.h"
#include "Brushes/SlateNoResource.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Styling/SlateStyleMacros.h"
#include "Styling/SlateStyleRegistry.h"
#include "Styling/SlateTypes.h"

#define RootToContentDir Style->RootToContentDir

TSharedPtr<FSlateStyleSet> FMantlePlaceEditorStyle::StyleInstance = nullptr;

void FMantlePlaceEditorStyle::Initialize()
{
	if (!StyleInstance.IsValid())
	{
		StyleInstance = Create();
		FSlateStyleRegistry::RegisterSlateStyle(*StyleInstance);
	}
}

void FMantlePlaceEditorStyle::Shutdown()
{
	if (StyleInstance.IsValid())
	{
		FSlateStyleRegistry::UnRegisterSlateStyle(*StyleInstance);
		ensure(StyleInstance.IsUnique());
		StyleInstance.Reset();
	}
}

const ISlateStyle& FMantlePlaceEditorStyle::Get()
{
	check(StyleInstance.IsValid());
	return *StyleInstance;
}

FName FMantlePlaceEditorStyle::GetStyleSetName()
{
	static const FName StyleSetName(TEXT("MantlePlaceEditorStyle"));
	return StyleSetName;
}

namespace
{
struct FFontSpec
{
	FMantlePlaceEditorStyle::EFont Role;
	const TCHAR* StyleName;
	const TCHAR* Typeface;
	uint16 Size;
	int16 LetterSpacing;
};

const FFontSpec FontSpecs[] = {
	{ FMantlePlaceEditorStyle::EFont::Wordmark, TEXT("MantlePlace.Font.Wordmark"), TEXT("Bold"),    11, -25 },
	{ FMantlePlaceEditorStyle::EFont::Heading,  TEXT("MantlePlace.Font.Heading"),  TEXT("Bold"),    14, 0 },
	{ FMantlePlaceEditorStyle::EFont::Title,    TEXT("MantlePlace.Font.Title"),    TEXT("Bold"),    11, 0 },
	{ FMantlePlaceEditorStyle::EFont::Body,     TEXT("MantlePlace.Font.Body"),     TEXT("Regular"), 10, 0 },
	{ FMantlePlaceEditorStyle::EFont::Small,    TEXT("MantlePlace.Font.Small"),    TEXT("Regular"), 9,  0 },
	{ FMantlePlaceEditorStyle::EFont::Mono,     TEXT("MantlePlace.Font.Mono"),     TEXT("Mono"),    9,  160 },
};

const FFontSpec& SpecFor(FMantlePlaceEditorStyle::EFont Role)
{
	for (const FFontSpec& Spec : FontSpecs)
	{
		if (Spec.Role == Role)
		{
			return Spec;
		}
	}
	checkNoEntry();
	return FontSpecs[0];
}

}

FSlateFontInfo FMantlePlaceEditorStyle::GetFont(EFont Role)
{
	return Get().GetFontStyle(FName(SpecFor(Role).StyleName));
}

FName FMantlePlaceEditorStyle::GetFontTypeface(EFont Role)
{
	return FName(SpecFor(Role).Typeface);
}

TSharedRef<FSlateStyleSet> FMantlePlaceEditorStyle::Create()
{
	using namespace MantlePlacePalette;

	TSharedRef<FSlateStyleSet> Style = MakeShared<FSlateStyleSet>(GetStyleSetName());

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
	if (Plugin.IsValid())
	{
		Style->SetContentRoot(Plugin->GetBaseDir() / TEXT("Resources"));
	}

	const FVector2D Icon16x16(16.0f, 16.0f);
	const FVector2D Mark32x32(32.0f, 32.0f);
	Style->Set("MantlePlace.TabIcon", new IMAGE_BRUSH(TEXT("MantlePlaceTabIcon"), Icon16x16));
	Style->Set("MantlePlace.Mark", new IMAGE_BRUSH(TEXT("MantlePlaceTabIcon"), Mark32x32));

	for (const FFontSpec& Spec : FontSpecs)
	{
		FSlateFontInfo Font = FCoreStyle::GetDefaultFontStyle(FName(Spec.Typeface), static_cast<float>(Spec.Size));
		Font.LetterSpacing = Spec.LetterSpacing;
		Style->Set(FName(Spec.StyleName), Font);
	}

	const float CardRadius = 8.0f;
	const float HairlinePx = 1.0f;

	Style->Set("MantlePlace.Card", new FSlateRoundedBoxBrush(
		FLinearColor::Transparent, CardRadius, Hairline(), HairlinePx));

	Style->Set("MantlePlace.Card.Hover", new FSlateRoundedBoxBrush(
		HoverFill(), CardRadius, Hairline(), HairlinePx));

	const FLinearColor FrameFill = Elevated() * FLinearColor(1, 1, 1, 0.35f);
	Style->Set("MantlePlace.Frame", new FSlateRoundedBoxBrush(
		FrameFill, CardRadius, Hairline(), HairlinePx));

	Style->Set("MantlePlace.TableRow", FTableRowStyle()
		.SetEvenRowBackgroundBrush(FSlateNoResource())
		.SetEvenRowBackgroundHoveredBrush(FSlateNoResource())
		.SetOddRowBackgroundBrush(FSlateNoResource())
		.SetOddRowBackgroundHoveredBrush(FSlateNoResource())
		.SetSelectorFocusedBrush(FSlateNoResource())
		.SetActiveBrush(FSlateNoResource())
		.SetActiveHoveredBrush(FSlateNoResource())
		.SetInactiveBrush(FSlateNoResource())
		.SetInactiveHoveredBrush(FSlateNoResource())
		.SetActiveHighlightedBrush(FSlateNoResource())
		.SetInactiveHighlightedBrush(FSlateNoResource())
		.SetDropIndicator_Above(FSlateNoResource())
		.SetDropIndicator_Below(FSlateNoResource())
		.SetDropIndicator_Onto(FSlateNoResource())
		.SetTextColor(FSlateColor(White()))
		.SetSelectedTextColor(FSlateColor(White())));

	Style->Set("MantlePlace.ListView", FTableViewStyle()
		.SetBackgroundBrush(FSlateNoResource()));

	Style->Set("MantlePlace.Pill", new FSlateRoundedBoxBrush(
		FLinearColor::White, FLinearColor::Transparent, 0.0f, FVector2f::ZeroVector));

	Style->Set("MantlePlace.Panel.Background", new FSlateColorBrush(Void()));

	auto WhiteA = [](float A) { return FLinearColor(1.0f, 1.0f, 1.0f, A); };

	auto PillBrush = [](const FLinearColor& Fill, const FLinearColor& Outline, float OutlineWidth)
	{
		return FSlateRoundedBoxBrush(Fill, Outline, OutlineWidth, FVector2f::ZeroVector);
	};

	Style->Set("MantlePlace.CardButton", FButtonStyle()
		.SetNormal(FSlateRoundedBoxBrush(FrameFill, CardRadius, Hairline(), HairlinePx))
		.SetHovered(FSlateRoundedBoxBrush(Elevated() * FLinearColor(1, 1, 1, 0.55f), CardRadius, WhiteA(0.28f), HairlinePx))
		.SetPressed(FSlateRoundedBoxBrush(Elevated() * FLinearColor(1, 1, 1, 0.70f), CardRadius, WhiteA(0.38f), HairlinePx))
		.SetNormalForeground(FSlateColor::UseForeground())
		.SetHoveredForeground(FSlateColor::UseForeground())
		.SetPressedForeground(FSlateColor::UseForeground())
		.SetNormalPadding(FMargin(0))
		.SetPressedPadding(FMargin(0)));

	Style->Set("MantlePlace.Button.Primary", FButtonStyle()
		.SetNormal(PillBrush(Mantle(), FLinearColor::Transparent, 0.0f))
		.SetHovered(PillBrush(MixToWhite(0xFF, 0x71, 0x10, 0.14f), WhiteA(0.35f), HairlinePx))
		.SetPressed(PillBrush(MixToWhite(0xFF, 0x71, 0x10, 0.24f), WhiteA(0.55f), HairlinePx))
		.SetNormalForeground(White())
		.SetHoveredForeground(White())
		.SetPressedForeground(White())
		.SetDisabled(PillBrush(Mantle() * FLinearColor(1, 1, 1, 0.4f), FLinearColor::Transparent, 0.0f))
		.SetDisabledForeground(White() * FLinearColor(1, 1, 1, 0.5f))
		.SetNormalPadding(FMargin(0))
		.SetPressedPadding(FMargin(0)));

	Style->Set("MantlePlace.Button.Secondary", FButtonStyle()
		.SetNormal(PillBrush(WhiteA(0.06f), WhiteA(0.20f), HairlinePx))
		.SetHovered(PillBrush(WhiteA(0.16f), WhiteA(0.45f), HairlinePx))
		.SetPressed(PillBrush(WhiteA(0.26f), WhiteA(0.60f), HairlinePx))
		.SetNormalForeground(White())
		.SetHoveredForeground(White())
		.SetPressedForeground(White())
		.SetDisabledForeground(White() * FLinearColor(1, 1, 1, 0.4f))
		.SetNormalPadding(FMargin(0))
		.SetPressedPadding(FMargin(0)));

	return Style;
}

#undef RootToContentDir
