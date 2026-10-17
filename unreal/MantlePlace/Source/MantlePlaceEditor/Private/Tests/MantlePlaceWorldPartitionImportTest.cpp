// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceDataLayers.h"
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
#include "UObject/ObjectSaveContext.h"
#include "WorldPartition/ActorDescContainer.h"
#include "WorldPartition/ActorDescContainerInstance.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionActorDescInstance.h"
#include "WorldPartition/WorldPartitionHelpers.h"
#include "DataLayer/DataLayerEditorSubsystem.h"
#include "WorldPartition/DataLayer/DataLayerAsset.h"
#include "WorldPartition/DataLayer/DataLayerInstance.h"
#include "WorldPartition/DataLayer/WorldDataLayers.h"

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
		Context.ContentRoot = TEXT("/Temp/MantlePlaceNonPartitionedTest");
		Context.Family = EMantlePlaceLayerFamily::Buildings;
		AStaticMeshActor* Mesh = World->SpawnActor<AStaticMeshActor>();
		const bool bLoadedBefore = Mesh != nullptr && Mesh->GetIsSpatiallyLoaded();
		Context.Claim(Mesh, TEXT("MP_NonPartitionedTestMesh"));
		TestEqual(TEXT("Claim leaves spatial loading alone outside World Partition"),
			Mesh != nullptr && Mesh->GetIsSpatiallyLoaded(), bLoadedBefore);
		TestTrue(TEXT("Claim assigns no Data Layer outside World Partition"),
			Mesh != nullptr && Mesh->GetDataLayerAssets().Num() == 0);
		TestNull(TEXT("and creates no Data Layer asset"),
			MantlePlaceDataLayers::FindAsset(Context.ContentRoot, EMantlePlaceLayerFamily::Buildings));
	}

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

// The Data Layer round trip: two orders imported into one partitioned world, each actor claimed by
// the import context's own Claim under its handler's family, then one order wiped by the
// orchestration's own wipe and imported again.
//
// What it pins:
//  - an import yields one populated Data Layer per family it spawned actors for, and no empty ones;
//  - every importer-spawned actor, a landscape's streaming proxies included, is in its family's layer;
//  - the layers are shared: two orders' buildings sit in the one Buildings layer;
//  - hiding a layer, as the Data Layers outliner does, hides exactly its family;
//  - a wipe takes its order's actors out of the shared layers, leaves the layer assets and instances
//    intact, and leaves zero dangling members; a re-import reuses the same layers.

namespace MantlePlaceWorldPartitionImportTest
{
int32 CountMembers(UWorld* World, const UDataLayerInstance* Instance)
{
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += (Instance != nullptr && IsValid(*It) && It->ContainsDataLayer(Instance)) ? 1 : 0;
	}
	return Count;
}

int32 CountTaggedMembers(UWorld* World, const UDataLayerInstance* Instance, const FString& Identity)
{
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += (Instance != nullptr && It->Tags.Contains(Tag) && It->ContainsDataLayer(Instance)) ? 1 : 0;
	}
	return Count;
}

int32 CountInstances(UWorld* World)
{
	int32 Count = 0;
	if (AWorldDataLayers* WorldDataLayers = World->GetWorldDataLayers())
	{
		WorldDataLayers->ForEachDataLayerInstance([&Count](UDataLayerInstance*) { ++Count; return true; });
	}
	return Count;
}

/** What the Data Layers outliner does on a visibility toggle, applied to this world's actors. */
void SetLayerVisible(UWorld* World, UDataLayerInstance* Instance, bool bVisible)
{
	UDataLayerEditorSubsystem* Subsystem = UDataLayerEditorSubsystem::Get();
	Subsystem->SetDataLayerVisibility(Instance, bVisible);
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		bool bSelectionChanged = false;
		bool bActorModified = false;
		Subsystem->UpdateActorVisibility(*It, bSelectionChanged, bActorModified, false, false);
	}
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionDataLayersTest,
	"MantlePlace.Import.WorldPartition.DataLayers",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionDataLayersTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionImportTest;
	using Family = EMantlePlaceLayerFamily;

	if (GEditor == nullptr || UDataLayerEditorSubsystem::Get() == nullptr)
	{
		AddInfo(TEXT("No editor: the Data Layer round trip needs an editor world. Skipped."));
		return true;
	}

	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceDataLayersTestWorld")), /*InWorldPackage*/ nullptr, /*bAddToRoot*/ true,
		ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a throwaway partitioned world"), World))
	{
		return false;
	}

	// Under /Temp, so the shared layer assets this makes are never offered for saving.
	const FString ContentRoot = TEXT("/Temp/MantlePlaceDataLayerTest");
	UMaterialInterface* Material = UMaterial::GetDefaultMaterial(MD_Surface);
	const int32 ExpectedProxies = MantlePlaceWorldPartitionLogic::ProxyCount(ComponentsPerAxis, ComponentsPerAxis, GridSize);

	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
	FMantlePlaceImportContext OrderA(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	OrderA.Identity = TEXT("aaaaaaaa-1111");
	OrderA.ContentRoot = ContentRoot;
	FMantlePlaceImportContext OrderB(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	OrderB.Identity = TEXT("bbbbbbbb-2222");
	OrderB.ContentRoot = ContentRoot;

	// Order A: a split landscape (Terrain) and a mesh (Buildings), each claimed under its family as
	// the orchestration sets it before the handler runs. Returns every actor the order spawned.
	TArray<TWeakObjectPtr<AActor>> TerrainA;
	TWeakObjectPtr<AActor> BuildingsA;
	const auto ImportOrderA = [&]()
	{
		TerrainA.Reset();
		OrderA.Family = Family::Terrain;
		ALandscape* Landscape = BuildFlatLandscape(World, Material);
		MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize);
		OrderA.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(MantlePlaceImportNaming::EActorKind::Landscape, OrderA.Identity));
		TerrainA.Add(Landscape);
		if (ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr)
		{
			for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
			{
				TerrainA.Add(Proxy.Get());
			}
		}
		OrderA.Family = Family::Buildings;
		AStaticMeshActor* Mesh = World->SpawnActor<AStaticMeshActor>();
		OrderA.Claim(Mesh, TEXT("MP_DataLayerTestBuildingsA"));
		BuildingsA = Mesh;
	};

	ImportOrderA();
	OrderB.Family = Family::Buildings;
	AStaticMeshActor* BuildingsB = World->SpawnActor<AStaticMeshActor>();
	OrderB.Claim(BuildingsB, TEXT("MP_DataLayerTestBuildingsB"));

	// --- One populated layer per family, shared, and no empty ones ---------------------------------
	UDataLayerInstance* Terrain = MantlePlaceDataLayers::Find(World, ContentRoot, Family::Terrain);
	UDataLayerInstance* Buildings = MantlePlaceDataLayers::Find(World, ContentRoot, Family::Buildings);
	UDataLayerAsset* TerrainAsset = MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Terrain);
	UDataLayerAsset* BuildingsAsset = MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Buildings);
	if (!TestNotNull(TEXT("the import made the Terrain layer"), Terrain)
		|| !TestNotNull(TEXT("the import made the Buildings layer"), Buildings))
	{
		World->DestroyWorld(false);
		return false;
	}
	TestNull(TEXT("no layer for a family that spawned nothing"), MantlePlaceDataLayers::Find(World, ContentRoot, Family::Roads));
	TestEqual(TEXT("one instance per family in the world"), CountInstances(World), 2);
	TestTrue(TEXT("a family layer is a Runtime layer"), Terrain->IsRuntime());
	TestTrue(TEXT("which a game run starts Activated, as the import was before layers"),
		Terrain->GetInitialRuntimeState() == EDataLayerRuntimeState::Activated);

	int32 TerrainAssigned = 0;
	for (const TWeakObjectPtr<AActor>& Actor : TerrainA)
	{
		TerrainAssigned += (Actor.IsValid() && Actor->ContainsDataLayer(Terrain) && !Actor->ContainsDataLayer(Buildings)) ? 1 : 0;
	}
	TestEqual(TEXT("the landscape and every proxy are in Terrain, and only Terrain"), TerrainAssigned, 1 + ExpectedProxies);
	TestTrue(TEXT("order A's mesh is in Buildings"), BuildingsA.IsValid() && BuildingsA->ContainsDataLayer(Buildings));
	TestTrue(TEXT("order B's mesh is in the SAME Buildings layer"), BuildingsB->ContainsDataLayer(Buildings));
	TestEqual(TEXT("Terrain holds exactly order A's terrain"), CountMembers(World, Terrain), 1 + ExpectedProxies);
	TestEqual(TEXT("Buildings holds both orders' buildings"), CountMembers(World, Buildings), 2);

	// --- Toggling a layer hides and shows exactly its family -------------------------------------
	SetLayerVisible(World, Terrain, false);
	int32 TerrainHidden = 0;
	for (const TWeakObjectPtr<AActor>& Actor : TerrainA)
	{
		TerrainHidden += (Actor.IsValid() && Actor->IsHiddenEd()) ? 1 : 0;
	}
	TestEqual(TEXT("hiding Terrain hides every terrain actor"), TerrainHidden, 1 + ExpectedProxies);
	TestFalse(TEXT("and not order A's buildings"), BuildingsA.IsValid() && BuildingsA->IsHiddenEd());
	TestFalse(TEXT("nor order B's"), BuildingsB->IsHiddenEd());
	SetLayerVisible(World, Terrain, true);
	TerrainHidden = 0;
	for (const TWeakObjectPtr<AActor>& Actor : TerrainA)
	{
		TerrainHidden += (Actor.IsValid() && Actor->IsHiddenEd()) ? 1 : 0;
	}
	TestEqual(TEXT("showing Terrain again shows every terrain actor"), TerrainHidden, 0);

	// --- Wipe order A: out of the shared layers, layers intact, zero dangling members --------------
	MantlePlaceImportOrchestration::ReplacePriorActors(World, OrderA.Identity, Timeline);
	int32 Surviving = BuildingsA.IsValid() ? 1 : 0;
	for (const TWeakObjectPtr<AActor>& Actor : TerrainA)
	{
		Surviving += Actor.IsValid() ? 1 : 0;
	}
	TestEqual(TEXT("wipe: none of order A's actors survives"), Surviving, 0);
	TestEqual(TEXT("wipe: zero dangling members of order A in Terrain"), CountTaggedMembers(World, Terrain, OrderA.Identity), 0);
	TestEqual(TEXT("wipe: zero dangling members of order A in Buildings"), CountTaggedMembers(World, Buildings, OrderA.Identity), 0);
	TestEqual(TEXT("wipe: Terrain is empty"), CountMembers(World, Terrain), 0);
	TestEqual(TEXT("wipe: Buildings keeps order B's mesh, and only it"), CountMembers(World, Buildings), 1);
	TestTrue(TEXT("wipe: the Terrain asset is intact"), MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Terrain) == TerrainAsset);
	TestTrue(TEXT("wipe: the Buildings asset is intact"), MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Buildings) == BuildingsAsset);
	TestTrue(TEXT("wipe: the Terrain instance is intact"), MantlePlaceDataLayers::Find(World, ContentRoot, Family::Terrain) == Terrain);
	TestEqual(TEXT("wipe: no instance removed"), CountInstances(World), 2);

	// --- Re-import order A: the same layers, reused -----------------------------------------------
	ImportOrderA();
	TestTrue(TEXT("re-import: reuses the Terrain instance"), MantlePlaceDataLayers::Find(World, ContentRoot, Family::Terrain) == Terrain);
	TestTrue(TEXT("re-import: reuses the Buildings asset"), MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Buildings) == BuildingsAsset);
	TestEqual(TEXT("re-import: still one instance per family"), CountInstances(World), 2);
	TestEqual(TEXT("re-import: Terrain populated again"), CountMembers(World, Terrain), 1 + ExpectedProxies);
	TestEqual(TEXT("re-import: Buildings holds both orders again"), CountMembers(World, Buildings), 2);

	// --- Wipe both: every layer empty, every layer intact ----------------------------------------
	MantlePlaceImportOrchestration::ReplacePriorActors(World, OrderA.Identity, Timeline);
	MantlePlaceImportOrchestration::ReplacePriorActors(World, OrderB.Identity, Timeline);
	TestEqual(TEXT("both wiped: Terrain has no members"), CountMembers(World, Terrain), 0);
	TestEqual(TEXT("both wiped: Buildings has no members"), CountMembers(World, Buildings), 0);
	TestEqual(TEXT("both wiped: the layers stay"), CountInstances(World), 2);

	// Let the in-memory test assets go with the world rather than linger in the editor session.
	for (UDataLayerAsset* Asset : { TerrainAsset, BuildingsAsset })
	{
		if (Asset != nullptr)
		{
			Asset->GetPackage()->SetDirtyFlag(false);
			Asset->ClearFlags(RF_Public | RF_Standalone);
		}
	}
	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

// The stale descriptor: a saved level keeps a World Partition descriptor for every actor a wipe
// destroyed until the next save deletes its package, and that descriptor reads as unloaded. A
// second re-import before the save must not count it as a prior actor in an unloaded region.
//
// The save is stood in for by the one engine event that gives an actor its descriptor, the
// container's pre-save hook, so nothing is written to disk.

namespace MantlePlaceWorldPartitionImportTest
{
int32 CountTaggedDescriptors(UWorld* World, const FString& Identity, bool bLoaded)
{
	UWorldPartition* WorldPartition = World->GetWorldPartition();
	if (WorldPartition == nullptr)
	{
		return 0;
	}
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	FWorldPartitionHelpers::ForEachActorDescInstance(WorldPartition, AActor::StaticClass(),
		[&Count, &Tag, bLoaded](const FWorldPartitionActorDescInstance* Desc)
		{
			Count += (Desc != nullptr && Desc->GetTags().Contains(Tag) && Desc->IsLoaded() == bLoaded) ? 1 : 0;
			return true;
		});
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionStaleDescriptorTest,
	"MantlePlace.Import.WorldPartition.StaleDescriptor",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionStaleDescriptorTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionImportTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the stale-descriptor check needs an editor world. Skipped."));
		return true;
	}

	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceStaleDescriptorTestWorld")), /*InWorldPackage*/ nullptr, /*bAddToRoot*/ true,
		ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a throwaway partitioned world"), World))
	{
		return false;
	}
	UWorldPartition* WorldPartition = World->GetWorldPartition();
	UActorDescContainerInstance* ContainerInstance =
		WorldPartition != nullptr ? WorldPartition->GetActorDescContainerInstance() : nullptr;
	UActorDescContainer* Container = ContainerInstance != nullptr ? ContainerInstance->GetContainer() : nullptr;
	if (!TestNotNull(TEXT("fixture: the world has an actor descriptor container"), Container))
	{
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		return false;
	}

	const FString Identity = TEXT("eeeeeeee-5555");
	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
	FMantlePlaceImportContext Context(World, Manifest, /*Reader*/ nullptr, Selection, Timeline, Result, Log);
	Context.Identity = Identity;

	// --- The prior import, as a save leaves it: a claimed actor with a descriptor -----------------
	AStaticMeshActor* Mesh = World->SpawnActor<AStaticMeshActor>();
	Context.Claim(Mesh, TEXT("MP_StaleDescriptorTestMesh"));
	if (!TestTrue(TEXT("fixture: the actor has its own package, as every actor in a partitioned level does"),
		Mesh != nullptr && Mesh->IsPackageExternal()))
	{
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		return false;
	}
	FObjectSaveContextData SaveData;
	Container->OnObjectPreSave(Mesh, FObjectPreSaveContext(SaveData));
	if (!TestEqual(TEXT("fixture: the stand-in save gives the actor a loaded descriptor"),
		CountTaggedDescriptors(World, Identity, /*bLoaded*/ true), 1))
	{
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		return false;
	}
	TestTrue(TEXT("a loaded prior actor is no refusal"),
		MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(World, Identity).IsEmpty());

	// --- The first re-import's wipe: the actor goes, its descriptor stays until a save -------------
	TestEqual(TEXT("the wipe destroys the prior actor"),
		MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline), 1);
	TestEqual(TEXT("fixture: its descriptor outlives it and reads as unloaded"),
		CountTaggedDescriptors(World, Identity, /*bLoaded*/ false), 1);

	// --- The second re-import, before any save --------------------------------------------------
	const FString Refusal = MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(World, Identity);
	TestTrue(*FString::Printf(TEXT("a destroyed actor's stale descriptor is no refusal (got: %s)"), *Refusal),
		Refusal.IsEmpty());

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
