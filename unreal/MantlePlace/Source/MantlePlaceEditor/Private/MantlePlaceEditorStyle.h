// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h"
#include "Layout/Margin.h"

class FSlateStyleSet;
class ISlateStyle;

class FMantlePlaceEditorStyle
{
public:
	enum class EFont : uint8
	{
		Wordmark,
		Heading,
		Title,
		Body,
		Small,
		Mono,
	};

	static constexpr float S1 = 32.f;
	static constexpr float S2 = 16.f;
	static constexpr float S3 = 8.f;
	static constexpr float S5 = 24.f;
	static constexpr float S6 = 4.f;

	static constexpr float Gutter = S2;

	static constexpr float ButtonPadV = 5.f;
	inline static const FMargin ButtonPaddingSm{ S3, ButtonPadV };
	inline static const FMargin ButtonPaddingMd{ S2, ButtonPadV };

	static void Initialize();
	static void Shutdown();

	static const ISlateStyle& Get();

	static FName GetStyleSetName();

	static FSlateFontInfo GetFont(EFont Role);

	static FName GetFontTypeface(EFont Role);

private:
	static TSharedRef<FSlateStyleSet> Create();
	static TSharedPtr<FSlateStyleSet> StyleInstance;
};
