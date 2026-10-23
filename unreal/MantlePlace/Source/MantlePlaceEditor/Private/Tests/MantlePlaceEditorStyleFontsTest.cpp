// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Fonts/CompositeFont.h"
#include "MantlePlaceEditorStyle.h"
#include "Styling/CoreStyle.h"

// The panel draws with the engine's own editor typefaces and ships no font files, so the plugin carries
// no third-party content and Fab's Third Party Software form does not apply. A role that pointed at a
// missing file or a misspelt typeface would not fail the panel: Slate quietly draws a fallback. This
// asserts what a user never sees fail: every MantlePlace.Font.* role resolves to the engine default
// font, at a typeface that font actually has.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceEditorStyleFontsTest,
    "MantlePlace.Editor.StyleFonts",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceEditorStyleFontsTest::RunTest(const FString& Parameters)
{
	using EFont = FMantlePlaceEditorStyle::EFont;

	const TCHAR* const RoleNames[] = { TEXT("Wordmark"), TEXT("Heading"), TEXT("Title"), TEXT("Body"), TEXT("Small"), TEXT("Mono") };
	const EFont Roles[] = { EFont::Wordmark, EFont::Heading, EFont::Title, EFont::Body, EFont::Small, EFont::Mono };

	const TSharedRef<const FCompositeFont> EngineDefault = FCoreStyle::GetDefaultFont();
	for (int32 Index = 0; Index < UE_ARRAY_COUNT(Roles); ++Index)
	{
		const FSlateFontInfo Font = FMantlePlaceEditorStyle::GetFont(Roles[Index]);
		const FName Typeface = FMantlePlaceEditorStyle::GetFontTypeface(Roles[Index]);

		TestTrue(FString::Printf(TEXT("%s font is the engine default font, not a bundled file"), RoleNames[Index]),
			Font.GetCompositeFont() == &EngineDefault.Get());
		TestEqual(FString::Printf(TEXT("%s font draws the role's typeface"), RoleNames[Index]), Font.TypefaceFontName, Typeface);

		bool bTypefaceExists = false;
		for (const FTypefaceEntry& Entry : EngineDefault->DefaultTypeface.Fonts)
		{
			bTypefaceExists |= (Entry.Name == Typeface);
		}
		TestTrue(FString::Printf(TEXT("%s typeface '%s' exists in the engine default font"), RoleNames[Index], *Typeface.ToString()),
			bTypefaceExists);
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
