// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceEditorStyle.h"

#include "MantlePlacePalette.h"
#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Fonts/CompositeFont.h" // FStandaloneCompositeFont (non-deprecated font-from-file path)
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

FSlateFontInfo FMantlePlaceEditorStyle::GetFont(EFont Role)
{
	static const TMap<EFont, FName> RoleToStyle = {
		{ EFont::Wordmark, TEXT("MantlePlace.Font.Wordmark") },
		{ EFont::Heading,  TEXT("MantlePlace.Font.Heading") },
		{ EFont::Title,    TEXT("MantlePlace.Font.Title") },
		{ EFont::Body,     TEXT("MantlePlace.Font.Body") },
		{ EFont::Small,    TEXT("MantlePlace.Font.Small") },
		{ EFont::Mono,     TEXT("MantlePlace.Font.Mono") },
	};
	return Get().GetFontStyle(RoleToStyle[Role]);
}

TSharedRef<FSlateStyleSet> FMantlePlaceEditorStyle::Create()
{
	using namespace MantlePlacePalette;

	TSharedRef<FSlateStyleSet> Style = MakeShared<FSlateStyleSet>(GetStyleSetName());

	// Assets (logo + fonts) live in the plugin's own Resources dir so the module is self-contained.
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
	if (Plugin.IsValid())
	{
		Style->SetContentRoot(Plugin->GetBaseDir() / TEXT("Resources"));
	}

	// ---- Icons / logo ----
	const FVector2D Icon16x16(16.0f, 16.0f);
	const FVector2D Logo32x32(32.0f, 32.0f);
	Style->Set("MantlePlace.TabIcon", new IMAGE_BRUSH(TEXT("MantlePlaceTabIcon"), Icon16x16));
	// Circular "mp" brand mark for the panel header (icon-512.png rendered at 32px).
	Style->Set("MantlePlace.Logo", new IMAGE_BRUSH(TEXT("MantlePlaceLogo"), Logo32x32));

	// ---- Embedded brand fonts (Montserrat + Geist Mono, OFL) ----
	// Slate font sizes are in points (~1.333 px at 100% app scale), so these track the web px scale.
	// LetterSpacing is in 1/1000 em (web tracking-tight = -0.025em; mp-sub = +0.16em).
	auto MakeFont = [&Style](const TCHAR* RelPath, uint16 Size, int16 LetterSpacing) -> FSlateFontInfo
	{
		// Wrap the .ttf in a standalone composite font (the non-deprecated font-from-file path; the raw
		// FSlateFontInfo(FString, ...) constructor is deprecated and slated for removal). RootToContentDir
		// is the SlateStyleMacros helper (#define'd to Style->RootToContentDir above).
		const TSharedRef<FCompositeFont> Composite = MakeShared<FStandaloneCompositeFont>(
			NAME_None, RootToContentDir(RelPath, TEXT(".ttf")), EFontHinting::Default, EFontLoadingPolicy::LazyLoad);
		FSlateFontInfo Font(Composite, static_cast<float>(Size));
		Font.LetterSpacing = LetterSpacing;
		return Font;
	};

	Style->Set("MantlePlace.Font.Wordmark", MakeFont(TEXT("Fonts/Montserrat-SemiBold"), 11, -25));
	Style->Set("MantlePlace.Font.Heading",  MakeFont(TEXT("Fonts/Montserrat-SemiBold"), 14, 0));
	Style->Set("MantlePlace.Font.Title",    MakeFont(TEXT("Fonts/Montserrat-SemiBold"), 11, 0));
	Style->Set("MantlePlace.Font.Body",     MakeFont(TEXT("Fonts/Montserrat-Medium"), 10, 0));
	Style->Set("MantlePlace.Font.Small",    MakeFont(TEXT("Fonts/Montserrat-Medium"), 9, 0));
	Style->Set("MantlePlace.Font.Mono",     MakeFont(TEXT("Fonts/GeistMono"), 9, 160));

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
