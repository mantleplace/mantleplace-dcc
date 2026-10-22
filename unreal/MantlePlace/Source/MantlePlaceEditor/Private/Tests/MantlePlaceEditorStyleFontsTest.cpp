// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Fonts/CompositeFont.h"
#include "HAL/PlatformFileManager.h"
#include "MantlePlaceEditorStyle.h"
#include "Misc/Paths.h"

// The brand fonts are third-party content, so they live only under Source/ThirdParty/Fonts (Fab's
// review requires it, and tools/unreal-naming/check_third_party_layout.py enforces it). A font that
// fails to load does not fail the panel: Slate quietly draws the engine default, so a moved file or a
// misspelt stem ships as a panel that looks subtly wrong. This asserts what a user never sees fail:
// every MantlePlace.Font.* role resolves to a file that exists, in the folder the review requires.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceEditorStyleFontsTest,
    "MantlePlace.Editor.StyleFonts",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceEditorStyleFontsTest::RunTest(const FString& Parameters)
{
	using EFont = FMantlePlaceEditorStyle::EFont;

	const TCHAR* const RoleNames[] = { TEXT("Wordmark"), TEXT("Heading"), TEXT("Title"), TEXT("Body"), TEXT("Small"), TEXT("Mono") };
	const EFont Roles[] = { EFont::Wordmark, EFont::Heading, EFont::Title, EFont::Body, EFont::Small, EFont::Mono };

	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	for (int32 Index = 0; Index < UE_ARRAY_COUNT(Roles); ++Index)
	{
		const FString Path = FMantlePlaceEditorStyle::GetFontFilePath(Roles[Index]);
		TestTrue(FString::Printf(TEXT("%s font is under Source/ThirdParty/Fonts: %s"), RoleNames[Index], *Path),
			FPaths::ConvertRelativePathToFull(Path).Contains(TEXT("/Source/ThirdParty/Fonts/")));
		TestTrue(FString::Printf(TEXT("%s font file exists: %s"), RoleNames[Index], *Path), Platform.FileExists(*Path));

		// The style set itself must point at that same file, not at some other root.
		const FSlateFontInfo Font = FMantlePlaceEditorStyle::GetFont(Roles[Index]);
		const FCompositeFont* Composite = Font.GetCompositeFont();
		if (!TestNotNull(FString::Printf(TEXT("%s font carries a composite font"), RoleNames[Index]), Composite))
		{
			continue;
		}
		if (TestTrue(FString::Printf(TEXT("%s font has a typeface entry"), RoleNames[Index]), Composite->DefaultTypeface.Fonts.Num() > 0))
		{
			TestTrue(FString::Printf(TEXT("%s style resolves to the moved file"), RoleNames[Index]),
				FPaths::IsSamePath(Composite->DefaultTypeface.Fonts[0].Font.GetFontFilename(), Path));
		}
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
