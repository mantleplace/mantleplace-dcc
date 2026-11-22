// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceAuthSystemBase.h"
#include "MantlePlaceBundleCache.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceManifestThreeLogic.h"
#include "MantlePlaceScriptedLevelLogic.h"
#include "MantlePlaceScriptedLevelRecorder.h"
#include "MantlePlaceVaultClient.h"
#include "MantlePlaceVaultImportOrchestrator.h"
#include "MantlePlaceVaultTypes.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"

namespace
{
FMantlePlaceLevelSize Size(const TCHAR* Level, int64 Bytes)
{
	FMantlePlaceLevelSize Out;
	Out.Level = Level;
	Out.SizeBytes = Bytes;
	return Out;
}

FMantlePlaceVaultItem ThreeOrder(const FString& OrderId, bool bOfferMin = true)
{
	FMantlePlaceVaultItem Item;
	Item.OrderId = OrderId;
	Item.bHasLevels = true;
	Item.RecommendedLevel = TEXT("Med");
	Item.Levels = { Size(TEXT("Max"), 4000000000), Size(TEXT("Med"), 1400000000) };
	if (bOfferMin)
	{
		Item.Levels.Add(Size(TEXT("Min"), 300000000));
	}
	return Item;
}

FMantlePlaceVaultItem TwoOrder(const FString& OrderId)
{
	FMantlePlaceVaultItem Item;
	Item.OrderId = OrderId;
	return Item;
}

struct FRequestedLevel
{
	FString OrderId;
	FString Level;
	TArray<FString> Files;
};

struct FScriptedFixture
{
	UMantlePlaceVaultImportOrchestrator* Orchestrator = nullptr;
	UMantlePlaceScriptedLevelRecorder* Recorder = nullptr;
	TSharedRef<TArray<FRequestedLevel>> Requests = MakeShared<TArray<FRequestedLevel>>();

	FScriptedFixture()
	{
		Orchestrator = NewObject<UMantlePlaceVaultImportOrchestrator>(GetTransientPackage());
		Recorder = NewObject<UMantlePlaceScriptedLevelRecorder>(GetTransientPackage());
		Orchestrator->Initialize(NewObject<UMantlePlaceAuthSystemBase>(Orchestrator));
		Orchestrator->OnImportPhase.AddDynamic(Recorder, &UMantlePlaceScriptedLevelRecorder::HandlePhase);
		Orchestrator->OnImportFinished.AddDynamic(Recorder, &UMantlePlaceScriptedLevelRecorder::HandleFinished);
		const TSharedRef<TArray<FRequestedLevel>> Sink = Requests;
		Orchestrator->GetVaultClient()->OnLevelRequestedNative.AddLambda(
			[Sink](const FString& OrderId, const FString& Level, const TArray<FString>& Files)
			{
				Sink->Add({ OrderId, Level, Files });
			});
	}

	bool SawPhaseMessage(const FString& Phase, const FString& Fragment) const
	{
		for (int32 i = 0; i < Recorder->Phases.Num(); ++i)
		{
			if (Recorder->Phases[i] == Phase && Recorder->Messages[i].Contains(Fragment))
			{
				return true;
			}
		}
		return false;
	}
};

void KeepLevelInSlot(const FMantlePlaceVaultItem& Item, const FString& Level)
{
	UMantlePlaceBundleCache* Cache = NewObject<UMantlePlaceBundleCache>(GetTransientPackage());
	const FString BundlePath = Cache->GetCachedBundlePath(Item);
	FFileHelper::SaveStringToFile(TEXT("not a zip"), *BundlePath);
	const FString Sidecar = FString::Printf(
		TEXT("{\"orderId\":\"%s\",\"localPath\":\"\",\"sha256\":\"\",\"sizeBytes\":0,\"manifestVersion\":\"\","
			"\"downloadedAtUtc\":\"\",\"format\":\"bundle\",\"holdsView\":false,\"listingSha256\":\"\",\"level\":\"%s\"}"),
		*Item.OrderId, *Level);
	FFileHelper::SaveStringToFile(Sidecar, *FPaths::Combine(FPaths::GetPath(BundlePath), TEXT("cache.json")));
}

void ClearSlot(const FMantlePlaceVaultItem& Item)
{
	NewObject<UMantlePlaceBundleCache>(GetTransientPackage())->EvictCache(Item);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceScriptedLevelRefusalTest,
	"MantlePlace.Vault.ScriptedLevel.Refusals",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceScriptedLevelRefusalTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceScriptedLevelLogic;
	const FMantlePlaceVaultItem Three = ThreeOrder(TEXT("order-3"));

	for (const TCHAR* Level : { TEXT("Max"), TEXT("Med"), TEXT("Min") })
	{
		TestEqual(FString::Printf(TEXT("%s is offered, so nothing is refused"), Level), FLogic::Refusal(Three, Level), FString());
	}

	const FString NotOffered = FLogic::Refusal(ThreeOrder(TEXT("order-3"), false), TEXT("Min"));
	TestTrue(TEXT("A level the order lacks is refused, naming the level"), NotOffered.Contains(TEXT("does not offer the Min level")));
	TestTrue(TEXT("and saying what it does offer"), NotOffered.Contains(TEXT("Max, Med")));
	TestFalse(TEXT("A word that is not a level is refused"), FLogic::Refusal(Three, TEXT("Ultra")).IsEmpty());
	TestFalse(TEXT("A level is case-sensitive, as the window's choice is"), FLogic::Refusal(Three, TEXT("min")).IsEmpty());
	TestFalse(TEXT("A 2.x token is not a level"), FLogic::Refusal(Three, TEXT("RAW")).IsEmpty());

	const FString TwoX = FLogic::Refusal(TwoOrder(TEXT("order-2")), TEXT("Med"));
	TestTrue(TEXT("A 2.x order is refused, saying it is not an MPB 3.0 order"), TwoX.Contains(TEXT("not an MPB 3.0 order")));
	TestTrue(TEXT("and where to import it"), TwoX.Contains(TEXT("StartVaultImport")));
	FMantlePlaceVaultItem NoLevelsRead = ThreeOrder(TEXT("order-3"));
	NoLevelsRead.Levels.Reset();
	TestFalse(TEXT("A 3.0 row whose levels were not read offers none"), FLogic::Refusal(NoLevelsRead, TEXT("Med")).IsEmpty());

	const FString Empty = FLogic::Refusal(Three, FString());
	TestTrue(TEXT("An empty level is refused, pointing at StartVaultImport"), Empty.Contains(TEXT("StartVaultImport")));
	TestTrue(TEXT("and at the recommendation it takes"), Empty.Contains(TEXT("recommends")));
	TestFalse(TEXT("on a 2.x order too"), FLogic::Refusal(TwoOrder(TEXT("order-2")), FString()).IsEmpty());
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceScriptedLevelEntryTest,
	"MantlePlace.Vault.ScriptedLevel.Entry",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceScriptedLevelEntryTest::RunTest(const FString& Parameters)
{
	UClass* OrchestratorClass = UMantlePlaceVaultImportOrchestrator::StaticClass();
	const UFunction* Start = OrchestratorClass->FindFunctionByName(TEXT("StartVaultImportAtLevel"));
	if (TestNotNull(TEXT("StartVaultImportAtLevel is a reflected function"), Start))
	{
		TestTrue(TEXT("and Blueprint-callable, so editor Python reaches it"), Start->HasAnyFunctionFlags(FUNC_BlueprintCallable));
	}
	const UFunction* Why = OrchestratorClass->FindFunctionByName(TEXT("GetLevelImportRefusal"));
	if (TestNotNull(TEXT("GetLevelImportRefusal is a reflected function"), Why))
	{
		TestTrue(TEXT("and Blueprint-callable"), Why->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure));
	}
	TestTrue(TEXT("The orchestrator is a Blueprint type, which Python constructs"), OrchestratorClass->GetBoolMetaData(TEXT("BlueprintType")));
	const FProperty* ResultLevel = FMantlePlaceImportResult::StaticStruct()->FindPropertyByName(TEXT("Level"));
	if (TestNotNull(TEXT("The import result carries the level it downloaded"), ResultLevel))
	{
		TestTrue(TEXT("readable from a script"), ResultLevel->HasAnyPropertyFlags(CPF_BlueprintVisible));
	}

	const FString Unique = FGuid::NewGuid().ToString(EGuidFormats::Digits);

	{
		FScriptedFixture Fixture;
		const FMantlePlaceVaultItem Two = TwoOrder(TEXT("mpscriptedtwo") + Unique);
		TestFalse(TEXT("A level on a 2.x order is refused"),
			Fixture.Orchestrator->StartVaultImportAtLevel(Two, EMantlePlaceImportMode::Landscape, TEXT("unreal"), TEXT("Min")));
		TestTrue(TEXT("with the reason, as an error phase"), Fixture.SawPhaseMessage(TEXT("Error"), TEXT("not an MPB 3.0 order")));

		const FMantlePlaceVaultItem NoMin = ThreeOrder(TEXT("mpscriptednomin") + Unique, false);
		TestFalse(TEXT("A level the order does not offer is refused"),
			Fixture.Orchestrator->StartVaultImportAtLevel(NoMin, EMantlePlaceImportMode::Landscape, TEXT("unreal"), TEXT("Min")));
		TestTrue(TEXT("with the reason"), Fixture.SawPhaseMessage(TEXT("Error"), TEXT("does not offer the Min level")));

		TestFalse(TEXT("An empty level is refused"),
			Fixture.Orchestrator->StartVaultImportAtLevel(ThreeOrder(TEXT("mpscriptedempty") + Unique), EMantlePlaceImportMode::Landscape, TEXT("unreal"), FString()));
		TestTrue(TEXT("with the reason"), Fixture.SawPhaseMessage(TEXT("Error"), TEXT("StartVaultImport")));

		TestEqual(TEXT("No request was made for any of them"), Fixture.Requests->Num(), 0);
		TestEqual(TEXT("and no import ran or finished"), Fixture.Recorder->FinishedCount, 0);
		TestFalse(TEXT("so the orchestrator is free"), Fixture.Orchestrator->IsBusy());
		TestEqual(TEXT("The question is answerable without starting anything"),
			UMantlePlaceVaultImportOrchestrator::GetLevelImportRefusal(Two, TEXT("Min")),
			FMantlePlaceScriptedLevelLogic::Refusal(Two, TEXT("Min")));
	}

	{
		const FMantlePlaceVaultItem Item = ThreeOrder(TEXT("mpscriptedfresh") + Unique);
		ClearSlot(Item);
		FScriptedFixture Fixture;
		Fixture.Orchestrator->StartVaultImportAtLevel(Item, EMantlePlaceImportMode::Landscape, TEXT("unreal"), TEXT("Min"));
		if (TestEqual(TEXT("One level request was made"), Fixture.Requests->Num(), 1))
		{
			const FRequestedLevel& Request = (*Fixture.Requests)[0];
			TestEqual(TEXT("for the order"), Request.OrderId, Item.OrderId);
			TestEqual(TEXT("naming the level the script named"), Request.Level, FString(TEXT("Min")));
			TestTrue(TEXT("and naming some files"), Request.Files.Num() > 0);
			TestTrue(TEXT("Unreal's files, the ones the import window's Download names"),
				Request.Files == FMantlePlaceManifestThreeLogic::RequestedFiles());
		}
		TestTrue(TEXT("The flow narrates the level it requests"), Fixture.SawPhaseMessage(TEXT("Downloading"), TEXT("Requesting the Min level")));
		TestEqual(TEXT("The import finished once"), Fixture.Recorder->FinishedCount, 1);
		TestFalse(TEXT("unsuccessfully, as there is no session"), Fixture.Recorder->bFinishedSuccess);
		TestTrue(TEXT("saying why"), Fixture.Recorder->Result.Message.Contains(TEXT("Not signed in")));
		TestTrue(TEXT("It downloaded nothing, so it reports no level"), Fixture.Recorder->Result.Level.IsEmpty());
		TestFalse(TEXT("and the orchestrator is free"), Fixture.Orchestrator->IsBusy());
	}

	{
		const FMantlePlaceVaultItem Item = ThreeOrder(TEXT("mpscriptedkept") + Unique);
		KeepLevelInSlot(Item, TEXT("Min"));

		{
			FScriptedFixture Fixture;
			Fixture.Orchestrator->StartVaultImportAtLevel(Item, EMantlePlaceImportMode::Landscape, TEXT("unreal"), TEXT("Min"));
			TestEqual(TEXT("The level the slot holds makes no request"), Fixture.Requests->Num(), 0);
			TestTrue(TEXT("It is imported again with no download, as the window's Download does"),
				Fixture.SawPhaseMessage(TEXT("Downloading"), TEXT("The Min level is already downloaded.")));
			TestEqual(TEXT("The import finished once"), Fixture.Recorder->FinishedCount, 1);
			TestEqual(TEXT("and the result says which level it imported"), Fixture.Recorder->Result.Level, FString(TEXT("Min")));
		}
		{
			FScriptedFixture Fixture;
			Fixture.Orchestrator->StartVaultImportAtLevel(Item, EMantlePlaceImportMode::Landscape, TEXT("unreal"), TEXT("Med"));
			if (TestEqual(TEXT("Another level is a request: the slot is not served for it"), Fixture.Requests->Num(), 1))
			{
				TestEqual(TEXT("for that level"), (*Fixture.Requests)[0].Level, FString(TEXT("Med")));
			}
			TestFalse(TEXT("and no kept level is announced"), Fixture.SawPhaseMessage(TEXT("Downloading"), TEXT("already downloaded")));
		}
		ClearSlot(Item);
	}
	return true;
}

#endif
