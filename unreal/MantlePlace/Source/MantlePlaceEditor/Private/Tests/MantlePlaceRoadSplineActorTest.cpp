// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceRoadSpline.h"

#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Engine/World.h"
#include "EngineUtils.h"

// The typed road actor, with no bundle: that what the platform publishes for a road is real,
// Blueprint-visible properties, that the actor's root is its spline, and that the importer's one
// registration (Claim) is all it takes for a re-import to replace it. The attribute decoding that
// feeds it is FMantlePlaceRoadSplinesLogic's and is covered in its own suite; the live-bundle test
// covers the whole path.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceRoadSplineActorTest,
	"MantlePlace.Import.RoadSplineActor",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceRoadSplineActorTest::RunTest(const FString& Parameters)
{
	// --- The attributes are real properties, readable from a Blueprint, not strings to parse ---------
	{
		const UClass* RoadClass = AMantlePlaceRoadSpline::StaticClass();
		const TCHAR* const Properties[] = {TEXT("Spline"), TEXT("RoadClass"), TEXT("RoadName"), TEXT("EstimatedWidthM")};
		for (const TCHAR* Name : Properties)
		{
			const FProperty* Property = RoadClass->FindPropertyByName(FName(Name));
			if (TestNotNull(FString::Printf(TEXT("road spline has a %s property"), Name), Property))
			{
				TestTrue(
					FString::Printf(TEXT("%s is readable from a Blueprint"), Name),
					Property->HasAnyPropertyFlags(CPF_BlueprintVisible));
			}
		}
		TestTrue(TEXT("the actor type is an actor, so the outliner selects and filters it by class"),
			RoadClass->IsChildOf(AActor::StaticClass()));

		// The defaults say "the platform published nothing", never a made-up value.
		const AMantlePlaceRoadSpline* Defaults = GetDefault<AMantlePlaceRoadSpline>();
		TestEqual(TEXT("an unpublished class is None"), Defaults->RoadClass, FName(NAME_None));
		TestTrue(TEXT("an unpublished name is empty"), Defaults->RoadName.IsEmpty());
		TestEqual(TEXT("an unpublished width is 0"), Defaults->EstimatedWidthM, 0.0f);
		TestNotNull(TEXT("the spline exists on the default object, so every spawn has one"), Defaults->Spline.Get());
	}

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: spawning and replacing a road spline needs an editor world. Skipped."));
		return true;
	}
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceRoadSplineTestWorld")));
	if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
	{
		return false;
	}

	const FString Identity = TEXT("aaaaaaaa-1111");
	const FString OtherIdentity = TEXT("bbbbbbbb-2222");
	const FName ImportTag(*MantlePlaceImportNaming::ImportTag(Identity));

	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportSelection Selection;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	FMantlePlaceImportContext Context(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Context.Identity = Identity;

	// --- A spawned road: its root is its spline, and the properties hold what was set ----------------
	AMantlePlaceRoadSpline* Road = World->SpawnActor<AMantlePlaceRoadSpline>();
	if (!TestNotNull(TEXT("a road spline spawns"), Road))
	{
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		return false;
	}
	TestTrue(TEXT("the spline is the actor's root"), Road->GetRootComponent() == Road->Spline.Get());
	Road->RoadClass = FName(TEXT("residential"));
	Road->RoadName = TEXT("Elm Street");
	Road->EstimatedWidthM = 6.5f;
	TestEqual(TEXT("class is read back as a property"), Road->RoadClass, FName(TEXT("residential")));
	TestEqual(TEXT("name is read back as a property"), Road->RoadName, FString(TEXT("Elm Street")));
	TestEqual(TEXT("width is read back as a property"), Road->EstimatedWidthM, 6.5f);

	// --- Claiming is the whole registration, and it writes no metadata tag --------------------------
	Context.Claim(Road, MantlePlaceImportNaming::RoadSplineLabel(Identity, 0));
	TestEqual(TEXT("the label is the generated road label"), Road->GetActorLabel(),
		MantlePlaceImportNaming::RoadSplineLabel(Identity, 0));
	TestEqual(TEXT("the actor carries exactly one tag"), Road->Tags.Num(), 1);
	TestTrue(TEXT("and it is the import tag a re-import matches on"), Road->Tags.Contains(ImportTag));

	// --- Re-import replaces it, and leaves another order's road alone ---------------------------------
	AMantlePlaceRoadSpline* Other = World->SpawnActor<AMantlePlaceRoadSpline>();
	Other->Tags.Add(FName(*MantlePlaceImportNaming::ImportTag(OtherIdentity)));
	TestEqual(TEXT("the re-import sweep removes the typed road of the same order"),
		MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline), 1);
	int32 Remaining = 0;
	for (TActorIterator<AMantlePlaceRoadSpline> It(World); It; ++It)
	{
		++Remaining;
	}
	TestEqual(TEXT("only the other order's road is left"), Remaining, 1);

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
