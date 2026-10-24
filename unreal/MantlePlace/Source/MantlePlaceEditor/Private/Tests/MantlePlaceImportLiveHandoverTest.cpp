// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceHandover.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceOdblLogic.h"

#include "Components/TextRenderComponent.h"
#include "Editor.h"
#include "Engine/TextRenderActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/PlatformFileManager.h"
#include "HAL/PlatformMisc.h"
#include "Misc/Paths.h"

// MantlePlace.Import.LiveHandover: the ODbL container and Prepare for handover against a real bundle.
//
// MantlePlace.Import.Odbl.Handover proves the mechanics on stand-in actors; this proves them on what a
// real import puts in a level. The bundle is MP_LIVETEST_BUNDLE (else the in-tree live-test copy), the
// same one MantlePlace.Import.LiveFreeTier reads, and the same skip rule: no bundle warns and passes
// locally, and is an error when CI is set.
//
// After the import, every actor carrying the ODbL tag sits in its order's ODbL outliner folder (the
// labelled container). Prepare for handover then removes every one of them, leaves no ODbL tag in the
// level, and writes one credit actor per order whose imported content recorded a credit, holding each
// line that content recorded. A bundle whose ODbL content imports nothing (a layer refused by name,
// say) still runs the credit half; the count of ODbL actors is logged so a run that removed nothing
// says so.

namespace MantlePlaceImportLiveHandoverTest
{
/** Every credit line the import recorded on this order's actors, from the tags Claim wrote. */
TArray<FString> RecordedCredit(UWorld* World, const FName& ImportTag)
{
	TArray<FString> Lines;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (!It->Tags.Contains(ImportTag))
		{
			continue;
		}
		TArray<FString> TagStrings;
		for (const FName& Tag : It->Tags)
		{
			TagStrings.Add(Tag.ToString());
		}
		for (const FString& Line : MantlePlaceOdblLogic::CreditLinesFromTags(TagStrings))
		{
			Lines.AddUnique(Line);
		}
	}
	return Lines;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportLiveHandoverTest,
	"MantlePlace.Import.LiveHandover",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportLiveHandoverTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportLiveHandoverTest;

	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	FString Zip = FPlatformMisc::GetEnvironmentVariable(TEXT("MP_LIVETEST_BUNDLE"));
	if (Zip.IsEmpty() || !Platform.FileExists(*Zip))
	{
		Zip = FPaths::ProjectSavedDir() / TEXT("MantlePlace/livetest/download.zip");
	}
	if (!Platform.FileExists(*Zip))
	{
		const FString Reason =
			FString::Printf(TEXT("Sample bundle not present (%s); skipping live handover test."), *Zip);
		if (!FPlatformMisc::GetEnvironmentVariable(TEXT("CI")).IsEmpty())
		{
			AddError(Reason);
			return false;
		}
		AddWarning(Reason);
		return true;
	}

	const FMantlePlaceImportResult Result =
		UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);
	if (!TestTrue(TEXT("import reported success"), Result.bSuccess))
	{
		AddInfo(Result.Message);
		return false;
	}

	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		AddError(TEXT("No editor world available."));
		return false;
	}

	// The labelled container: every ODbL actor of this order is in the order's ODbL folder.
	const FName ImportTag(*MantlePlaceImportNaming::ImportTag(Result.Identity));
	const FName OdblTag = MantlePlaceImportNaming::OdblTag();
	const FName OdblFolder(*MantlePlaceImportNaming::OdblFolder(Result.Identity));
	TArray<FString> OdblLabels;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (!It->Tags.Contains(ImportTag) || !It->Tags.Contains(OdblTag))
		{
			continue;
		}
		OdblLabels.Add(It->GetActorLabel());
		TestEqual(*FString::Printf(TEXT("ODbL actor \"%s\" is in the order's ODbL folder"), *It->GetActorLabel()),
			It->GetFolderPath(), OdblFolder);
	}
	const TArray<FString> Credit = RecordedCredit(World, ImportTag);
	AddInfo(FString::Printf(TEXT("import: %d actor(s), %d of them ODbL in folder \"%s\"; %d credit line(s) recorded"),
		Result.CreatedActors.Num(), OdblLabels.Num(), *OdblFolder.ToString(), Credit.Num()));

	// Prepare for handover, through the command's own entry point.
	const FMantlePlaceHandoverReport Report = MantlePlaceHandover::Prepare(World);
	AddInfo(Report.Summary());

	TestEqual(TEXT("Prepare for handover removed every ODbL actor the import made"), Report.Removed.Num(), OdblLabels.Num());
	int32 OdblLeft = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		OdblLeft += It->Tags.Contains(OdblTag) ? 1 : 0;
	}
	TestEqual(TEXT("no ODbL-tagged actor is left in the level"), OdblLeft, 0);

	if (Credit.Num() > 0)
	{
		const FName CreditTag(*MantlePlaceImportNaming::CreditActorTag(Result.Identity));
		const ATextRenderActor* CreditActor = nullptr;
		for (TActorIterator<ATextRenderActor> It(World); It; ++It)
		{
			if (It->Tags.Contains(CreditTag))
			{
				CreditActor = *It;
			}
		}
		if (TestNotNull(TEXT("Prepare for handover wrote the order's credit actor"), CreditActor))
		{
			const FString Text = CreditActor->GetTextRender() != nullptr
				? CreditActor->GetTextRender()->Text.ToString()
				: FString();
			AddInfo(FString::Printf(TEXT("credit actor \"%s\": %s"), *CreditActor->GetActorLabel(), *Text));
			for (const FString& Line : Credit)
			{
				TestTrue(*FString::Printf(TEXT("the credit actor carries \"%s\""), *Line), Text.Contains(Line));
			}
		}
	}
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
