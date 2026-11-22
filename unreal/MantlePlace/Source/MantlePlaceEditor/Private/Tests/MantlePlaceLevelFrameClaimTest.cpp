// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLevelFrameLogic.h"

#include "Editor.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLevelFrameClaimTest,
	"MantlePlace.Import.LevelFrameClaim",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace MantlePlaceLevelFrameClaimTest
{
FMantlePlaceVaultManifest MakeManifest(double EastingM, double NorthingM, const TCHAR* Datum)
{
	FMantlePlaceVaultManifest Manifest;
	Manifest.bValid = true;
	Manifest.Georef.CrsProjected = TEXT("EPSG:32613");
	Manifest.Georef.OriginEastingM = EastingM;
	Manifest.Georef.OriginNorthingM = NorthingM;
	Manifest.Georef.GroundOrthometricHM = 1500.0;
	Manifest.Georef.bHasOrigin = true;
	Manifest.SiteVerticalDatum = Datum;
	return Manifest;
}

AStaticMeshActor* ImportOne(UWorld* World, FMantlePlaceVaultManifest& Manifest, const TCHAR* Identity, FMantlePlaceFramePlan& OutPlan)
{
	FMantlePlaceFrameOrigin Origin;
	Origin.Crs = Manifest.Georef.CrsProjected;
	Origin.VerticalDatum = Manifest.SiteVerticalDatum;
	Origin.EastingM = Manifest.Georef.OriginEastingM;
	Origin.NorthingM = Manifest.Georef.OriginNorthingM;
	Origin.bStated = Manifest.Georef.bHasOrigin;
	OutPlan = FMantlePlaceLevelFrameLogic::Plan(Identity, Origin, MantlePlaceImportOrchestration::ReadLevelImports(World));
	Manifest.SetLevelOffsetM(OutPlan.OffsetEastM, OutPlan.OffsetNorthM);

	FMantlePlaceImportSelection Selection;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	FMantlePlaceImportContext Context(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Context.Identity = Identity;
	Context.FrameTag = OutPlan.FrameValue;

	AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(Manifest.GetMeshLocation(), FMantlePlaceVaultManifest::GetMeshRotation());
	Context.Claim(Actor, FString::Printf(TEXT("MP_Test_%s"), Identity));
	return Actor;
}

void Remove(UWorld* World, const TCHAR* Identity)
{
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	TArray<AActor*> Doomed;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (It->Tags.Contains(Tag))
		{
			Doomed.Add(*It);
		}
	}
	for (AActor* Actor : Doomed)
	{
		World->EditorDestroyActor(Actor, false);
	}
}
}

bool FMantlePlaceLevelFrameClaimTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceLevelFrameClaimTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the level frame test needs an editor world. Skipped."));
		return true;
	}
	AddExpectedError(TEXT("World has no context"), EAutomationExpectedErrorFlags::Contains, 0);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, false, FName(TEXT("MantlePlaceLevelFrameTestWorld")));
	if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
	{
		return false;
	}

	FMantlePlaceVaultManifest A = MakeManifest(441959.5, 4014372.5, TEXT("NAVD88 (GEOID18)"));
	FMantlePlaceVaultManifest B = MakeManifest(443459.5, 4014172.5, TEXT("NAVD88 (GEOID18)"));
	FMantlePlaceVaultManifest C = MakeManifest(444959.5, 4014472.5, TEXT("NAVD88 (GEOID18)"));
	FMantlePlaceVaultManifest Far = MakeManifest(441959.5 + 250000.0, 4014372.5, TEXT("NAVD88 (GEOID18)"));
	FMantlePlaceFramePlan Plan;

	TestEqual(TEXT("an empty level reads no imports"), MantlePlaceImportOrchestration::ReadLevelImports(World).Num(), 0);

	AStaticMeshActor* ActorA = ImportOne(World, A, TEXT("orderA"), Plan);
	TestTrue(TEXT("first import anchors"), Plan.Outcome == EMantlePlaceFrameOutcome::Anchor);
	TestTrue(TEXT("anchor at the world origin"), ActorA->GetActorLocation().Equals(FVector(0.0, 0.0, 150000.0), 1e-3));
	TestTrue(TEXT("the actor carries the frame tag"),
		ActorA->Tags.ContainsByPredicate([](const FName& Tag) { return Tag.ToString().StartsWith(MantlePlaceImportNaming::FrameTagPrefix()); }));

	AStaticMeshActor* ActorB = ImportOne(World, B, TEXT("orderB"), Plan);
	TestTrue(TEXT("second import is offset"), Plan.Outcome == EMantlePlaceFrameOutcome::Offset);
	TestTrue(TEXT("second import beside the first"), ActorB->GetActorLocation().Equals(FVector(-20000.0, 150000.0, 150000.0), 1e-3));

	AStaticMeshActor* ActorC = ImportOne(World, C, TEXT("orderC"), Plan);
	TestTrue(TEXT("third import against the first"), ActorC->GetActorLocation().Equals(FVector(10000.0, 300000.0, 150000.0), 1e-3));

	Remove(World, TEXT("orderB"));
	AStaticMeshActor* ActorB2 = ImportOne(World, B, TEXT("orderB"), Plan);
	TestTrue(TEXT("re-imported second stays in place"), ActorB2->GetActorLocation().Equals(FVector(-20000.0, 150000.0, 150000.0), 1e-3));
	Remove(World, TEXT("orderA"));
	AStaticMeshActor* ActorA2 = ImportOne(World, A, TEXT("orderA"), Plan);
	TestTrue(TEXT("re-imported anchor bundle stays at zero"), ActorA2->GetActorLocation().Equals(FVector(0.0, 0.0, 150000.0), 1e-3));

	AStaticMeshActor* ActorFar = ImportOne(World, Far, TEXT("orderFar"), Plan);
	TestTrue(TEXT("too far: centred"), Plan.Outcome == EMantlePlaceFrameOutcome::Centred && !Plan.Warning.IsEmpty());
	TestTrue(TEXT("too far: at the world origin"), ActorFar->GetActorLocation().Equals(FVector(0.0, 0.0, 150000.0), 1e-3));
	Remove(World, TEXT("orderFar"));

	Remove(World, TEXT("orderA"));
	Remove(World, TEXT("orderB"));
	Remove(World, TEXT("orderC"));
	TestEqual(TEXT("no import left"), MantlePlaceImportOrchestration::ReadLevelImports(World).Num(), 0);
	AStaticMeshActor* Fresh = ImportOne(World, B, TEXT("orderB"), Plan);
	TestTrue(TEXT("the anchor cleared: the next import anchors"), Plan.Outcome == EMantlePlaceFrameOutcome::Anchor);
	TestTrue(TEXT("and sits at the world origin"), Fresh->GetActorLocation().Equals(FVector(0.0, 0.0, 150000.0), 1e-3));

	World->DestroyWorld(false);
	return true;
}

#endif
