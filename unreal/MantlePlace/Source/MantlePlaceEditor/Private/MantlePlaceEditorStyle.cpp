// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceEditorStyle.h"

#include "MantlePlacePalette.h"
#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Styling/CoreStyle.h" // FCoreStyle::GetDefaultFontStyle (the engine's own editor fonts)
#include "Brushes/SlateNoResource.h" // FSlateNoResource (transparent row brushes)
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Styling/SlateStyleMacros.h"
#include "Styling/SlateStyleRegistry.h"
#include "Styling/SlateTypes.h" // FButtonStyle / FTableRowStyle

// IMAGE_BRUSH (from SlateStyleMacros.h) resolves paths via a local variable named `Style`.
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
/** One font role: its style name, the engine default typeface it draws with, and its metrics. */
struct FFontSpec
{
	FMantlePlaceEditorStyle::EFont Role;
	const TCHAR* StyleName;
	const TCHAR* Typeface; // a typeface of the engine default font (Regular, Bold, Mono)
	uint16 Size;           // points
	int16 LetterSpacing;   // 1/1000 em
};

// Slate font sizes are in points (~1.333 px at 100% app scale), so these track the web px scale.
// LetterSpacing is in 1/1000 em (web tracking-tight = -0.025em; mp-sub = +0.16em).
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

} // namespace

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

	// The icons (our own work) live in the plugin's own Resources dir so the module is self-contained.
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
	if (Plugin.IsValid())
	{
		Style->SetContentRoot(Plugin->GetBaseDir() / TEXT("Resources"));
	}

	// ---- Icons / logo ----
	// Both brushes draw the same 64px file: the square "mp" mark, monogram only, rendered by
	// tools/brand-assets/render_mark.py. One source at 64px covers the 16px tab and the 32px
	// header with headroom for a high-DPI editor, and downsamples cleanly from a power of two.
	// The mark drops its extrude at these sizes and the roundel this once shipped is retired --
	// docs/adr/0009-host-assets-render-the-monogram.md says why, before anyone restores either.
	const FVector2D Icon16x16(16.0f, 16.0f);
	const FVector2D Mark32x32(32.0f, 32.0f);
	Style->Set("MantlePlace.TabIcon", new IMAGE_BRUSH(TEXT("MantlePlaceTabIcon"), Icon16x16));
	Style->Set("MantlePlace.Mark", new IMAGE_BRUSH(TEXT("MantlePlaceTabIcon"), Mark32x32));

	// ---- Fonts: the engine's own editor typefaces ----
	// The plugin ships no font files: a bundled third-party font would have to sit under
	// Source/ThirdParty and be declared on Fab's Third Party Software form. The roles keep the web
	// type scale's sizes and tracking (see FontSpecs) and draw with the engine default font.
	for (const FFontSpec& Spec : FontSpecs)
	{
		FSlateFontInfo Font = FCoreStyle::GetDefaultFontStyle(FName(Spec.Typeface), static_cast<float>(Spec.Size));
		Font.LetterSpacing = Spec.LetterSpacing;
		Style->Set(FName(Spec.StyleName), Font);
	}

	// ---- Rounded / hairline surfaces (mirror .curator-frame + the glass hairline) ----
	const float CardRadius = 8.0f;   // web --r-sm
	const float HairlinePx = 1.0f;

	// Card: transparent fill + a 0.5px-feel white/15% hairline outline (radius 8).
	Style->Set("MantlePlace.Card", new FSlateRoundedBoxBrush(
		FLinearColor::Transparent, CardRadius, Hairline(), HairlinePx));

	// Card hover: same outline + a faint white/4% fill.
	Style->Set("MantlePlace.Card.Hover", new FSlateRoundedBoxBrush(
		HoverFill(), CardRadius, Hairline(), HairlinePx));

	// Detail frame / list card resting fill: a single cool (elevated @ low alpha) hairline surface used by
	// BOTH the detail-page sections and the list-page cards, so the "bento" boxes read identically across
	// states (no warm editor tint on one and cool onyx on the other).
	const FLinearColor FrameFill = Elevated() * FLinearColor(1, 1, 1, 0.35f);
	Style->Set("MantlePlace.Frame", new FSlateRoundedBoxBrush(
		FrameFill, CardRadius, Hairline(), HairlinePx));

	// Transparent table-row style for the vault list: the editor's default TableView.Row paints a warm
	// even/odd + hover background that showed THROUGH the card button's transparent fill, making the list
	// cards read warmer than the detail frames. Zero every row brush so the card carries the whole look.
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

	// Transparent list-view container: STableViewBase::OnPaint fills the whole list rect (rows + the empty
	// space below them) with FTableViewStyle::BackgroundBrush, and the editor's default "ListView" style
	// is an OPAQUE FSlateColorBrush(Recessed) - that warm grey sits one layer below the rows, so the
	// transparent TableRow above can't remove it. FSlateNoResource (DrawAs=NoDrawType) makes OnPaint skip
	// the box entirely, letting the panel's onyx Void show through (matching the detail scroll body).
	Style->Set("MantlePlace.ListView", FTableViewStyle()
		.SetBackgroundBrush(FSlateNoResource()));

	// Pill: white-filled capsule; call sites tint it via BorderBackgroundColor. Uses HalfHeightRadius
	// rounding (the no-radius ctor) - a huge FIXED radius renders degenerate (see the button note below).
	Style->Set("MantlePlace.Pill", new FSlateRoundedBoxBrush(
		FLinearColor::White, FLinearColor::Transparent, 0.0f, FVector2f::ZeroVector));

	// Panel backdrop: the web's onyx surface, so the tool reads as the site rather than the editor grey.
	Style->Set("MantlePlace.Panel.Background", new FSlateColorBrush(Void()));

	// ---- Button states -------------------------------------------------------------------------------
	// The web's hover/press is a white overlay (10% / 18%) on a lighter glass surface. On this flat onyx
	// panel that same overlay is nearly invisible, so we (a) step the fill up and (b) also brighten the
	// pill's OUTLINE on hover/press - the outline ring is the crisp cue the flat fill alone lacked. White
	// overlays are alpha-only (gamma-independent), so FLinearColor(1,1,1,A) is the correct linear value.
	auto WhiteA = [](float A) { return FLinearColor(1.0f, 1.0f, 1.0f, A); };

	// Pill buttons round with HalfHeightRadius (a true capsule that clamps to the button's height). A
	// huge FIXED corner radius (e.g. 999) is DEGENERATE in the rounded-box shader: the corner arcs
	// exceed the button half-height, coverage collapses to zero, and neither the fill nor the outline
	// ever draw - which is why every pill button (Sign In / Refresh / Browse / Import) was rendering as
	// bare text. The no-radius FSlateRoundedBoxBrush ctor selects HalfHeightRadius; Cards/Frames keep
	// their small radius-8 (well within bounds, so they were always fine).
	auto PillBrush = [](const FLinearColor& Fill, const FLinearColor& Outline, float OutlineWidth)
	{
		return FSlateRoundedBoxBrush(Fill, Outline, OutlineWidth, FVector2f::ZeroVector);
	};

	// Card: a clickable frame - the whole bundle card opens its details page. At rest it carries the SAME
	// cool FrameFill + hairline as the detail-page sections (so the bento boxes match across states); hover
	// / press lift the elevated fill a little and brighten the ring for the interactive cue.
	Style->Set("MantlePlace.CardButton", FButtonStyle()
		.SetNormal(FSlateRoundedBoxBrush(FrameFill, CardRadius, Hairline(), HairlinePx))
		.SetHovered(FSlateRoundedBoxBrush(Elevated() * FLinearColor(1, 1, 1, 0.55f), CardRadius, WhiteA(0.28f), HairlinePx))
		.SetPressed(FSlateRoundedBoxBrush(Elevated() * FLinearColor(1, 1, 1, 0.70f), CardRadius, WhiteA(0.38f), HairlinePx))
		.SetNormalForeground(FSlateColor::UseForeground())
		.SetHoveredForeground(FSlateColor::UseForeground())
		.SetPressedForeground(FSlateColor::UseForeground())
		.SetNormalPadding(FMargin(0))
		.SetPressedPadding(FMargin(0)));

	// Primary (Import): bg-mp-mantle. Hover/press brighten the fill (colourless white overlay, no
	// brand-hue jump) AND ring the pill with a white outline so the state clearly reads on the dark panel.
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

	// Secondary (Sign in/out, Refresh, Browse, Back): a clearly-visible hairline pill at rest (faint fill
	// + outline so it reads as a button, not bare text) that fills in and brightens its ring on hover/press.
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
