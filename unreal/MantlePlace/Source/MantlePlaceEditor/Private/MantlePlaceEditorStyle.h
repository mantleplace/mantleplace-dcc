// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h" // FSlateFontInfo (returned by GetFont)
#include "Layout/Margin.h"       // FMargin (button-padding tokens)

class FSlateStyleSet;
class ISlateStyle;

/**
 * Slate style set + design tokens for the Mantle Place editor tooling. Beyond the Window-menu / tab
 * logo icon it now carries the vault panel's small design system - the embedded brand fonts
 * (Montserrat + Geist Mono, from Resources/Fonts) and the rounded/hairline card + pill + logo brushes
 * that reproduce the website's vault aesthetic. Colors live in MantlePlacePalette.h. Mirrors Cesium's
 * CesiumStyleSet: Initialize() in StartupModule, Shutdown() in ShutdownModule.
 */
class FMantlePlaceEditorStyle
{
public:
	/** Named font roles mapped to the web type scale (see GetFont). */
	enum class EFont : uint8
	{
		Wordmark,  // Montserrat SemiBold, tight tracking - the "mantle / place" header lockup
		Heading,   // Montserrat SemiBold ~ web h3 (20px) - card codename / detail title
		Title,     // Montserrat SemiBold - section headings
		Body,      // Montserrat Medium  ~ web b2 (14px) - primary body text
		Small,     // Montserrat Medium  ~ web b3 (12px) - secondary text
		Mono,      // Geist Mono         ~ web mp-sub (10px) - meta lines / eyebrows / pill labels
	};

	// ---- Spacing scale (Slate units) - mirrors the web Vault's two-value gutter system ----------
	// The web Curator Vault reduces to ONE rule: 16 around / between / inside cards, 8 within a card
	// (docs/design.md S3.2; globals.css --spacing-s1/s2/s3 = 32/16/8). These tokens name that scale;
	// S2 and S3 carry nearly all real usage, the others round it out.
	static constexpr float S1 = 32.f; // web s1 - between major sections (rare here)
	static constexpr float S2 = 16.f; // web s2 - THE GUTTER: content edges, frame/card gaps, card inner padding
	static constexpr float S3 = 8.f;  // web s3 - fine rhythm within a card/tier, button gaps, fact rows
	static constexpr float S5 = 24.f; // web s5 - medium step (was the old native Gutter; kept for opt-out)
	static constexpr float S6 = 4.f;  // web s6 - hairline/optical nudge (legacy "avoid")

	// Back-compat alias. WAS 24; now points at the web gutter (16). Every existing call site that used
	// Gutter for an edge or an inter-box gap therefore shifts 24 -> 16 with this one edit. Prefer S2 in
	// new code. To revert the panel to the old roomier 24px gutter, flip this to S5 (single knob).
	static constexpr float Gutter = S2;

	// ---- Button content padding (a COMPONENT metric, deliberately OFF the margin scale) ----------
	// Slate has no "h-8" height primitive: a pill's height == label height + 2*vertical padding. The
	// vertical value is tuned to land the pill ~32px tall with the brand font and must stay uniform
	// across every button, so it is a fixed constant, NOT a scale token. Horizontal mirrors the web's
	// two button sizes: sm = 8 (dense), md = 16 (roomy).
	static constexpr float ButtonPadV = 5.f;
	inline static const FMargin ButtonPaddingSm{ S3, ButtonPadV }; // dense - h-pad 8
	inline static const FMargin ButtonPaddingMd{ S2, ButtonPadV }; // roomy - h-pad 16

	static void Initialize();
	static void Shutdown();

	/** The registered style set (brushes + fonts). Prefer this over FSlateStyleRegistry lookups. */
	static const ISlateStyle& Get();

	/** Style-set name, used as the FSlateIcon style-set argument. */
	static FName GetStyleSetName();

	/** A brand font at the given role (embedded Montserrat / Geist Mono). */
	static FSlateFontInfo GetFont(EFont Role);

private:
	static TSharedRef<FSlateStyleSet> Create();
	static TSharedPtr<FSlateStyleSet> StyleInstance;
};
