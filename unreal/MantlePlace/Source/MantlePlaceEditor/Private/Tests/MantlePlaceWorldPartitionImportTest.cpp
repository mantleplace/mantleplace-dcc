// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLandscapeImporter.h"
#include "MantlePlaceWorldPartitionLogic.h"

#include "Editor.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "Landscape.h"
#include "LandscapeInfo.h"
#include "LandscapeStreamingProxy.h"
#include "Materials/Material.h"

// The World Partition round trip, without a bundle: a landscape built the way the importer builds
// one, in a partitioned throwaway world, split by the importer's own split, claimed by the import
// context's own Claim, then wiped by the orchestration's own wipe and built again.
//
// What it pins, each one a thing a World Partition level gets wrong silently:
//  - the split yields the proxy count the component grid and the grid size say it should;
//  - every proxy carries the import tag, so the tag-matched wipe can see it;
//  - the material assigned before Import() is the one every component renders after the split;
//  - the wipe destroys the proxies before their parent, and leaves no tagged actor behind;
//  - an importer-spawned non-landscape actor is kept always loaded (v1), the proxies are not;
//  - a re-import after the wipe yields the same split again, and a second wipe leaves nothing.
//
// The landscape is flat on purpose: placement is the live test's to assert, against a real bundle
// (MantlePlace.Import.LiveFreeTier). This one asserts the World Partition mechanics only.

namespace MantlePlaceWorldPartitionImportTest
{
// 4x4 components of one 7-quad section each: 29 vertices a side, the smallest landscape that
// still splits into more than one proxy on the engine's default grid of 2.
constexpr int32 ComponentsPerAxis = 4;
constexpr int32 QuadsPerSection = 7;
constexpr int32 GridSize = 2;

ALandscape* BuildFlatLandscape(UWorld* World, UMaterialInterface* Material)
{
	const int32 Size = ComponentsPerAxis * QuadsPerSection + 1;
	ALandscape* Landscape = World->SpawnActor<ALandscape>(FVector::ZeroVector, FRotator::ZeroRotator);
	if (Landscape == nullptr)
	{
		return nullptr;
	}
	// As the importer does: the material before Import(), so the components are built with it.
	Landscape->LandscapeMaterial = Material;

	TArray<uint16> Heights;
	Heights.Init(32768, Size * Size);
	TMap<FGuid, TArray<uint16>> HeightDataPerLayers;
	HeightDataPerLayers.Add(FGuid(), MoveTemp(Heights));
	TMap<FGuid, TArray<FLandscapeImportLayerInfo>> MaterialLayerDataPerLayers;
	MaterialLayerDataPerLayers.Add(FGuid(), TArray<FLandscapeImportLayerInfo>());

	Landscape->Import(FGuid::NewGuid(), 0, 0, Size - 1, Size - 1, /*NumSubsections*/ 1, QuadsPerSection,
		HeightDataPerLayers, TEXT(""), MaterialLayerDataPerLayers, ELandscapeImportAlphamapType::Additive,
		TArrayView<const FLandscapeLayer>());
	return Landscape;
}

int32 CountProxies(ALandscape* Landscape)
{
	ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr;
	return Info != nullptr ? Info->GetSortedStreamingProxies().Num() : 0;
}

int32 CountTagged(UWorld* World, const FString& Identity)
{
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += It->Tags.Contains(Tag) ? 1 : 0;
	}
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionImportTest,
	"MantlePlace.Import.WorldPartition.RoundTrip",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionImportTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionImportTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the World Partition round trip needs an editor world. Skipped."));
		return true;
	}

	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceWorldPartitionTestWorld")), /*InWorldPackage*/ nullptr, /*bAddToRoot*/ true,
		ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a throwaway partitioned world"), World))
	{
		return false;
	}
	if (!TestTrue(TEXT("fixture: the world is partitioned"), UWorld::IsPartitionedWorld(World)))
	{
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		return false;
	}

	UMaterialInterface* Material = UMaterial::GetDefaultMaterial(MD_Surface);
	const FString Identity = TEXT("cccccccc-3333");
	const int32 ExpectedProxies =
		MantlePlaceWorldPartitionLogic::ProxyCount(ComponentsPerAxis, ComponentsPerAxis, GridSize);

	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
	FMantlePlaceImportContext Context(World, Manifest, /*Reader*/ nullptr, Selection, Timeline, Result, Log);
	Context.Identity = Identity;

	// The destroy order, as the editor announces it.
	TArray<UClass*> Destroyed;
	const FDelegateHandle DeletedHandle = GEngine->OnLevelActorDeleted().AddLambda(
		[&Destroyed](AActor* Actor) { Destroyed.Add(Actor != nullptr ? Actor->GetClass() : nullptr); });

	for (int32 Round = 0; Round < 2; ++Round)
	{
		const FString Pass = Round == 0 ? TEXT("import") : TEXT("re-import");

		// --- Import ---------------------------------------------------------------------------
		ALandscape* Landscape = BuildFlatLandscape(World, Material);
		if (!TestNotNull(*FString::Printf(TEXT("%s: the landscape is built"), *Pass), Landscape))
		{
			break;
		}
		TestTrue(*FString::Printf(TEXT("%s: the split runs in a partitioned world"), *Pass),
			MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize));
		Context.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(
			MantlePlaceImportNaming::EActorKind::Landscape, Identity));
		AStaticMeshActor* Mesh = World->SpawnActor<AStaticMeshActor>();
		Context.Claim(Mesh, TEXT("MP_WorldPartitionTestMesh"));

		TestEqual(*FString::Printf(TEXT("%s: the proxy count is the component grid over the grid size"), *Pass),
			CountProxies(Landscape), ExpectedProxies);
		TestEqual(*FString::Printf(TEXT("%s: the parent and every proxy carry the import tag, and the mesh"), *Pass),
			CountTagged(World, Identity), 1 + ExpectedProxies + 1);
		TestFalse(*FString::Printf(TEXT("%s: the landscape parent is not spatially loaded"), *Pass),
			Landscape->GetIsSpatiallyLoaded());
		if (ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			int32 SpatiallyLoaded = 0;
			for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
			{
				SpatiallyLoaded += (Proxy.IsValid() && Proxy->GetIsSpatiallyLoaded()) ? 1 : 0;
			}
			TestEqual(*FString::Printf(TEXT("%s: every proxy streams"), *Pass), SpatiallyLoaded, ExpectedProxies);

			int32 Components = 0;
			int32 WrongMaterial = 0;
			Info->ForAllLandscapeComponents([&Components, &WrongMaterial, Material](ULandscapeComponent* Component)
			{
				++Components;
				WrongMaterial += Component->GetLandscapeMaterial() != Material ? 1 : 0;
			});
			TestEqual(*FString::Printf(TEXT("%s: every component survived the split"), *Pass),
				Components, ComponentsPerAxis * ComponentsPerAxis);
			TestEqual(*FString::Printf(TEXT("%s: every component renders the material assigned before Import"), *Pass),
				WrongMaterial, 0);
		}
		TestTrue(*FString::Printf(TEXT("%s: an importer-spawned mesh is kept always loaded (v1)"), *Pass),
			Mesh != nullptr && !Mesh->GetIsSpatiallyLoaded());

		// --- Wipe -----------------------------------------------------------------------------
		TestTrue(*FString::Printf(TEXT("%s: nothing of the prior import is unloaded, so no refusal"), *Pass),
			MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(World, Identity).IsEmpty());
		Destroyed.Reset();
		const int32 Removed = MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline);
		TestEqual(*FString::Printf(TEXT("%s: the wipe removes the parent, its proxies and the mesh"), *Pass),
			Removed, 1 + ExpectedProxies + 1);
		TestEqual(*FString::Printf(TEXT("%s: zero orphans, no tagged actor survives"), *Pass),
			CountTagged(World, Identity), 0);
		int32 SurvivingProxies = 0;
		for (TActorIterator<ALandscapeStreamingProxy> It(World); It; ++It)
		{
			++SurvivingProxies;
		}
		TestEqual(*FString::Printf(TEXT("%s: no streaming proxy survives, tagged or not"), *Pass), SurvivingProxies, 0);

		const int32 ParentAt = Destroyed.IndexOfByKey(ALandscape::StaticClass());
		const int32 LastProxyAt = Destroyed.FindLastByPredicate(
			[](UClass* Class) { return Class == ALandscapeStreamingProxy::StaticClass(); });
		TestTrue(*FString::Printf(TEXT("%s: the parent is destroyed"), *Pass), ParentAt != INDEX_NONE);
		TestTrue(*FString::Printf(TEXT("%s: every proxy is destroyed before its parent"), *Pass),
			LastProxyAt != INDEX_NONE && LastProxyAt < ParentAt);
	}

	TestEqual(TEXT("a second wipe finds nothing"),
		MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline), 0);

	GEngine->OnLevelActorDeleted().Remove(DeletedHandle);
	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceNonPartitionedSplitTest,
	"MantlePlace.Import.WorldPartition.NonPartitionedUnchanged",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceNonPartitionedSplitTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionImportTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the non-partitioned check needs an editor world. Skipped."));
		return true;
	}
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceNonPartitionedTestWorld")));
	if (!TestNotNull(TEXT("a throwaway non-partitioned world"), World))
	{
		return false;
	}

	ALandscape* Landscape = BuildFlatLandscape(World, UMaterial::GetDefaultMaterial(MD_Surface));
	if (TestNotNull(TEXT("the landscape is built"), Landscape))
	{
		TestFalse(TEXT("the split does nothing in a non-partitioned level"),
			MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize));
		TestEqual(TEXT("so there are no proxies"), CountProxies(Landscape), 0);
		TestEqual(TEXT("and the landscape keeps every component"),
			Landscape->LandscapeComponents.Num(), ComponentsPerAxis * ComponentsPerAxis);

		FMantlePlaceVaultManifest Manifest;
		FMantlePlaceImportResult Result;
		TArray<FString> Log;
		MantlePlaceImportTiming::FTimeline Timeline;
		const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
		FMantlePlaceImportContext Context(World, Manifest, nullptr, Selection, Timeline, Result, Log);
		Context.Identity = TEXT("dddddddd-4444");
		AStaticMeshActor* Mesh = World->SpawnActor<AStaticMeshActor>();
		const bool bLoadedBefore = Mesh != nullptr && Mesh->GetIsSpatiallyLoaded();
		Context.Claim(Mesh, TEXT("MP_NonPartitionedTestMesh"));
		TestEqual(TEXT("Claim leaves spatial loading alone outside World Partition"),
			Mesh != nullptr && Mesh->GetIsSpatiallyLoaded(), bLoadedBefore);
	}

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
