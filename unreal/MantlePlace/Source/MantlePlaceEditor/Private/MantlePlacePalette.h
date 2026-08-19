// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "Math/Color.h"

/**
 * The Mantle Place web app's design tokens as Slate colors, so the
 * vault panel, its cards, the details page, and status pills all draw from one source of truth.
 *
 * The website is authored in sRGB hex; FLinearColor(FColor(...)) applies the sRGB->linear decode so
 * the swatch displays on screen exactly as it does in the browser. Values return by-function (not
 * constexpr statics) because that decode is a runtime conversion.
 */
namespace MantlePlacePalette
{
	/** A web brand accent (sRGB hex) as it displays on screen, with an optional alpha. */
	inline FLinearColor Srgb(uint8 R, uint8 G, uint8 B, float A = 1.0f)
	{
		FLinearColor Color(FColor(R, G, B));
		Color.A = A;
		return Color;
	}

	/**
	 * Composite an opaque white overlay of fraction F over an sRGB base colour, mixed in sRGB display
	 * space (the way the web's hover/press overlays composite in CSS). Slate button brushes take a
	 * single solid fill, so the overlay has to be pre-baked into one colour rather than layered.
	 */
	inline FLinearColor MixToWhite(uint8 R, uint8 G, uint8 B, float F)
	{
		auto Mix = [F](uint8 C) -> uint8 { return static_cast<uint8>(C + (255.0f - C) * F + 0.5f); };
		return Srgb(Mix(R), Mix(G), Mix(B));
	}

	//~ Brand tokens (colors.css).
	inline FLinearColor Void()     { return Srgb(0x0B, 0x0C, 0x10); } // app background / onyx ink
	inline FLinearColor Surface()  { return Srgb(0x15, 0x16, 0x1A); } // chrome glass base
	inline FLinearColor Elevated() { return Srgb(0x22, 0x24, 0x2A); } // inner card fill base
	inline FLinearColor White()    { return Srgb(0xFF, 0xFF, 0xFF); }
	inline FLinearColor Water()    { return Srgb(0x09, 0x95, 0xB5); } // cyan  - live / info / "ready"
	inline FLinearColor Land()     { return Srgb(0x4C, 0x9A, 0x14); } // green - success / press
	inline FLinearColor Mantle()   { return Srgb(0xFF, 0x71, 0x10); } // orange- primary CTA / brand
	inline FLinearColor Outer()    { return Srgb(0xFF, 0xB3, 0x08); } // gold  - "processing"
	inline FLinearColor Crust()    { return Srgb(0x64, 0x2D, 0x05); } // dark walnut - web "generate" CTA (parity; unapplied)

	//~ Status neutrals (Tailwind defaults the web pills use).
	inline FLinearColor Zinc400()  { return Srgb(0xA1, 0xA1, 0xAA); }
	inline FLinearColor Zinc500()  { return Srgb(0x71, 0x71, 0x7A); }
	inline FLinearColor Red400()   { return Srgb(0xFF, 0x64, 0x67); }

	//~ Derived surface treatments (mirror the web's white-alpha overlays).
	/** 0.5px hairline outline used by .curator-frame (inset 0 0 0 0.5px white/15%). */
	inline FLinearColor Hairline()  { return White() * FLinearColor(1, 1, 1, 0.15f); }
	/** Faint card hover fill (hover:bg-mp-white/[0.04]). */
	inline FLinearColor HoverFill() { return White() * FLinearColor(1, 1, 1, 0.04f); }
	/** Subdued body text (~white/70) for meta / secondary labels. */
	inline FLinearColor Subtle()    { return White() * FLinearColor(1, 1, 1, 0.70f); }
	/** Very subdued text (~white/40) for the eyebrow / "sub" register. */
	inline FLinearColor Faint()     { return White() * FLinearColor(1, 1, 1, 0.40f); }
	/** The mp-sub eyebrow / mono-label default (~white/65). */
	inline FLinearColor Sub()       { return White() * FLinearColor(1, 1, 1, 0.65f); }
	/** Emphasised body / definition-list values (~white/85). */
	inline FLinearColor Strong()    { return White() * FLinearColor(1, 1, 1, 0.85f); }
}
