// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceAutomationLibrary.h"
#include "MantlePlaceCuratorExtentLogic.h"
#include "MantlePlaceExtentBox.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceLevelExtent.h"
#include "MantlePlaceVaultClient.h"

#include "Editor.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"

// The script entries of Open in curator (the ones editor Python calls), over throwaway worlds: a level
// with a previous import, a level with an Extent box, and an empty level. Each case compares the entry
// with what the button does -- MantlePlaceLevelExtent::Resolve and ::CuratorUrl, the two calls the panel
// makes -- so a copy of that code in the library would show here as a disagreement. The decisions
// themselves are asserted in MantlePlace.CuratorExtent.Sources and .Precedence.

namespace
{
	UWorld* NewWorld(const TCHAR* Name)
	{
		return UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false, FName(Name));
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceCuratorScriptEntryTest,
	"MantlePlace.CuratorExtent.ScriptEntry",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCuratorScriptEntryTest::RunTest(const FString& Parameters)
{
	// --- Reachable from editor Python: both entries are static Blueprint-callable functions ---------------
	for (const TCHAR* Name : { TEXT("ResolveCuratorExtent"), TEXT("PlaceExtentBox") })
	{
		const UFunction* Function = UMantlePlaceAutomationLibrary::StaticClass()->FindFunctionByName(FName(Name));
		if (TestNotNull(FString::Printf(TEXT("%s is a reflected function"), Name), Function))
		{
			TestTrue(FString::Printf(TEXT("%s is static and Blueprint-callable, so editor Python reaches it"), Name),
				Function->HasAnyFunctionFlags(FUNC_BlueprintCallable) && Function->HasAnyFunctionFlags(FUNC_Static));
		}
	}

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the entries read an editor world. Skipped."));
		return true;
	}

	// --- An empty level: no source, the plain curator link -----------------------------------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceScriptEntryEmptyWorld"));
		if (!TestNotNull(TEXT("a throwaway world"), World))
		{
			return false;
		}
		const FMantlePlaceCuratorResolution Result = UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(World);
		const FMantlePlaceResolvedExtent Button = MantlePlaceLevelExtent::Resolve(World);
		TestTrue(TEXT("an empty level has no source"), Result.Source == EMantlePlaceCuratorSource::None);
		TestFalse(TEXT("so no extent"), Result.bHasExtent);
		TestFalse(TEXT("the button would show its no-extent prompt"), Button.IsSet());
		TestEqual(TEXT("the extent is all zero"), Result.West + Result.South + Result.East + Result.North, 0.0);
		TestEqual(TEXT("the link is the button's"), Result.Url, MantlePlaceLevelExtent::CuratorUrl(Button));
		TestEqual(TEXT("it is the plain curator, which the prompt's Open the curator opens"),
			Result.Url, FMantlePlaceCuratorExtentLogic::BuildCuratorUrl(
				GetDefault<UMantlePlaceVaultClient>()->VaultApiBaseUrl, FMantlePlaceResolvedExtent()));
		TestFalse(TEXT("with no extent token"), Result.Url.Contains(TEXT("aoi=")));
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	// --- A level with an Extent box: placed by the entry, then located ----------------------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceScriptEntryBoxWorld"));
		if (!TestNotNull(TEXT("a throwaway world"), World))
		{
			return false;
		}
		AActor* Placed = UMantlePlaceAutomationLibrary::PlaceExtentBoxInWorld(World);
		AMantlePlaceExtentBox* Box = Cast<AMantlePlaceExtentBox>(Placed);
		if (TestNotNull(TEXT("the entry places an Extent box"), Box))
		{
			TestEqual(TEXT("it carries the label the prompt's box carries"), Box->GetActorLabel(), MantlePlaceImportNaming::ExtentBoxLabel());
			TestTrue(TEXT("and is the box the prompt's code returns on a second press"), MantlePlaceLevelExtent::PlaceExtentBox(World) == Box);
			TestTrue(TEXT("and the entry does the same"), UMantlePlaceAutomationLibrary::PlaceExtentBoxInWorld(World) == Box);
			TestEqual(TEXT("one box in the level"), MantlePlaceLevelExtent::ExtentBoxes(World).Num(), 1);
			TestFalse(TEXT("a box nobody located is no source"),
				UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(World).bHasExtent);

			Box->CenterLongitude = -97.7431;
			Box->CenterLatitude = 30.2672;
			const FMantlePlaceCuratorResolution Result = UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(World);
			const FMantlePlaceResolvedExtent Button = MantlePlaceLevelExtent::Resolve(World);
			TestTrue(TEXT("a located box is the source"), Result.Source == EMantlePlaceCuratorSource::ExtentBox);
			TestTrue(TEXT("with an extent"), Result.bHasExtent);
			TestEqual(TEXT("west"), Result.West, Button.Extent.West);
			TestEqual(TEXT("south"), Result.South, Button.Extent.South);
			TestEqual(TEXT("east"), Result.East, Button.Extent.East);
			TestEqual(TEXT("north"), Result.North, Button.Extent.North);
			TestEqual(TEXT("the source in words is the button's"), Result.SourceDescription,
				FMantlePlaceCuratorExtentLogic::DescribeSource(Button.Source));
			TestEqual(TEXT("the link is the one the button opens"), Result.Url, MantlePlaceLevelExtent::CuratorUrl(Button));
			TestTrue(TEXT("on the extent, from Unreal"), Result.Url.Contains(TEXT("#aoi=")) && Result.Url.EndsWith(TEXT("&from=unreal")));
		}
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	// --- A level with a previous import ------------------------------------------------------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceScriptEntryImportWorld"));
		if (!TestNotNull(TEXT("a throwaway world"), World))
		{
			return false;
		}
		const FString Identity = TEXT("00000552-aaaa");
		AActor* Imported = World->SpawnActor<AActor>();
		Imported->Tags.Add(FName(*MantlePlaceImportNaming::ImportTag(Identity)));

		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		Record.bHasExtent = true;
		Record.Extent.West = -97.75;
		Record.Extent.South = 30.25;
		Record.Extent.East = -97.73;
		Record.Extent.North = 30.27;
		MantlePlaceImportProvenance::Write(Record);

		// A located Extent box in the same level: the previous import still outranks it.
		AMantlePlaceExtentBox* Box = Cast<AMantlePlaceExtentBox>(UMantlePlaceAutomationLibrary::PlaceExtentBoxInWorld(World));
		if (TestNotNull(TEXT("an Extent box beside the import"), Box))
		{
			Box->CenterLongitude = -90.0;
			Box->CenterLatitude = 40.0;
		}

		const FMantlePlaceCuratorResolution Result = UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(World);
		const FMantlePlaceResolvedExtent Button = MantlePlaceLevelExtent::Resolve(World);
		TestTrue(TEXT("the previous import is the source"), Result.Source == EMantlePlaceCuratorSource::PriorImport);
		TestTrue(TEXT("with an extent"), Result.bHasExtent);
		TestEqual(TEXT("west is the record's"), Result.West, -97.75);
		TestEqual(TEXT("south is the record's"), Result.South, 30.25);
		TestEqual(TEXT("east is the record's"), Result.East, -97.73);
		TestEqual(TEXT("north is the record's"), Result.North, 30.27);
		TestEqual(TEXT("the link is the one the button opens"), Result.Url, MantlePlaceLevelExtent::CuratorUrl(Button));
		TestTrue(TEXT("and carries the record's extent"), Result.Url.Contains(TEXT("#aoi=-97.75,30.25,-97.73,30.27&from=unreal")));

		IFileManager::Get().Delete(*MantlePlaceImportProvenance::RecordPath(Identity), /*bRequireExists*/ false, /*bEvenReadOnly*/ true);
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	// --- The no-argument entry reads the editor's world, as the button does -------------------------------
	{
		UWorld* Editor = MantlePlaceLevelExtent::EditorWorld();
		if (TestNotNull(TEXT("the editor has a world"), Editor))
		{
			const FMantlePlaceCuratorResolution Scripted = UMantlePlaceAutomationLibrary::ResolveCuratorExtent();
			const FMantlePlaceCuratorResolution InWorld = UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(Editor);
			TestTrue(TEXT("the same source as the editor world read directly"), Scripted.Source == InWorld.Source);
			TestEqual(TEXT("and the same link"), Scripted.Url, InWorld.Url);
		}
	}

	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
