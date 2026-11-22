// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "Math/Color.h"

namespace MantlePlacePalette
{
	inline FLinearColor Srgb(uint8 R, uint8 G, uint8 B, float A = 1.0f)
	{
		FLinearColor Color(FColor(R, G, B));
		Color.A = A;
		return Color;
	}

	inline FLinearColor MixToWhite(uint8 R, uint8 G, uint8 B, float F)
	{
		auto Mix = [F](uint8 C) -> uint8 { return static_cast<uint8>(C + (255.0f - C) * F + 0.5f); };
		return Srgb(Mix(R), Mix(G), Mix(B));
	}

	inline FLinearColor Void()     { return Srgb(0x0B, 0x0C, 0x10); }
	inline FLinearColor Surface()  { return Srgb(0x15, 0x16, 0x1A); }
	inline FLinearColor Elevated() { return Srgb(0x22, 0x24, 0x2A); }
	inline FLinearColor White()    { return Srgb(0xFF, 0xFF, 0xFF); }
	inline FLinearColor Water()    { return Srgb(0x09, 0x95, 0xB5); }
	inline FLinearColor Land()     { return Srgb(0x4C, 0x9A, 0x14); }
	inline FLinearColor Mantle()   { return Srgb(0xFF, 0x71, 0x10); }
	inline FLinearColor Outer()    { return Srgb(0xFF, 0xB3, 0x08); }
	inline FLinearColor Crust()    { return Srgb(0x64, 0x2D, 0x05); }

	inline FLinearColor Zinc400()  { return Srgb(0xA1, 0xA1, 0xAA); }
	inline FLinearColor Zinc500()  { return Srgb(0x71, 0x71, 0x7A); }
	inline FLinearColor Red400()   { return Srgb(0xFF, 0x64, 0x67); }

	inline FLinearColor Hairline()  { return White() * FLinearColor(1, 1, 1, 0.15f); }
	inline FLinearColor HoverFill() { return White() * FLinearColor(1, 1, 1, 0.04f); }
	inline FLinearColor Subtle()    { return White() * FLinearColor(1, 1, 1, 0.70f); }
	inline FLinearColor Faint()     { return White() * FLinearColor(1, 1, 1, 0.40f); }
	inline FLinearColor Sub()       { return White() * FLinearColor(1, 1, 1, 0.65f); }
	inline FLinearColor Strong()    { return White() * FLinearColor(1, 1, 1, 0.85f); }
}
