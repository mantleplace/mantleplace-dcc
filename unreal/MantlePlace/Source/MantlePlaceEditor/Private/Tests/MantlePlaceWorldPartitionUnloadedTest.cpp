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
#include "MantlePlaceTestMount.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Editor.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/FileManager.h"
#include "Landscape.h"
#include "LandscapeInfo.h"
#include "LandscapeStreamingProxy.h"
#include "MaterialDomain.h"
#include "Materials/Material.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/UObjectHash.h"
#include "UObject/UObjectIterator.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionActorDescInstance.h"
#include "WorldPartition/WorldPartitionHelpers.h"
#include "WorldPartition/WorldPartitionSubsystem.h"

namespace MantlePlaceWorldPartitionUnloadedTest
{
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
	Landscape->LandscapeMaterial = Material;
	TArray<uint16> Heights;
	Heights.Init(32768, Size * Size);
	TMap<FGuid, TArray<uint16>> HeightDataPerLayers;
	HeightDataPerLayers.Add(FGuid(), MoveTemp(Heights));
	TMap<FGuid, TArray<FLandscapeImportLayerInfo>> MaterialLayerDataPerLayers;
	MaterialLayerDataPerLayers.Add(FGuid(), TArray<FLandscapeImportLayerInfo>());
	Landscape->Import(FGuid::NewGuid(), 0, 0, Size - 1, Size - 1, 1, QuadsPerSection,
		HeightDataPerLayers, TEXT(""), MaterialLayerDataPerLayers, ELandscapeImportAlphamapType::Additive,
		TArrayView<const FLandscapeLayer>());
	return Landscape;
}

int32 CountTaggedInLevel(UWorld* World, const FString& Identity)
{
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += It->Tags.Contains(Tag) ? 1 : 0;
	}
	return Count;
}

int32 CountProxiesInLevel(UWorld* World)
{
	int32 Count = 0;
	for (TActorIterator<ALandscapeStreamingProxy> It(World); It; ++It)
	{
		++Count;
	}
	return Count;
}

int32 CountUnloadedTaggedDescriptors(UWorld* World, const FString& Identity)
{
	UWorldPartition* WorldPartition = World->GetWorldPartition();
	if (WorldPartition == nullptr)
	{
		return 0;
	}
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	FWorldPartitionHelpers::ForEachActorDescInstance(WorldPartition, AActor::StaticClass(),
		[&Count, &Tag](const FWorldPartitionActorDescInstance* Desc)
		{
			if (Desc != nullptr && Desc->GetTags().Contains(Tag) && !Desc->IsLoaded())
			{
				const UPackage* Package = FindPackage(nullptr, *Desc->GetActorPackage().ToString());
				Count += (Package == nullptr || !Package->IsDirty()) ? 1 : 0;
			}
			return true;
		});
	return Count;
}

struct FPartlyUnloadedLevel
{
	explicit FPartlyUnloadedLevel(const TCHAR* InName)
		: Name(InName)
		, Mount(InName)
	{
	}

	~FPartlyUnloadedLevel()
	{
		if (World != nullptr)
		{
			World->DestroyWorld(false);
			World->RemoveFromRoot();
		}
		for (TObjectIterator<UPackage> It; It; ++It)
		{
			if (Mount.Owns(It->GetName()))
			{
				It->SetDirtyFlag(false);
				ForEachObjectWithPackage(*It, [](UObject* Object) { Object->ClearFlags(RF_Public | RF_Standalone); return true; });
			}
		}
		CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS);
		Mount.Unmount();
	}

	bool SaveAll()
	{
		TArray<UPackage*> Packages;
		for (TObjectIterator<UPackage> It; It; ++It)
		{
			if (Mount.Owns(It->GetName()) && (It->IsDirty() || *It == World->GetPackage()))
			{
				Packages.Add(*It);
			}
		}
		const UPackage* MapPackage = World->GetPackage();
		Packages.Sort([MapPackage](const UPackage& A, const UPackage& B) { return &A == MapPackage && &B != MapPackage; });
		TArray<FString> Saved;
		bool bAll = true;
		for (UPackage* Package : Packages)
		{
			const bool bMap = Package == MapPackage;
			const FString Filename = FPackageName::LongPackageNameToFilename(Package->GetName(),
				bMap ? FPackageName::GetMapPackageExtension() : FPackageName::GetAssetPackageExtension());
			FSavePackageArgs Args;
			Args.TopLevelFlags = RF_Public | RF_Standalone;
			Args.SaveFlags = SAVE_NoError;
			if (UPackage::SavePackage(Package, bMap ? World : nullptr, *Filename, Args))
			{
				Saved.Add(Filename);
			}
			else
			{
				bAll = false;
			}
		}
		IAssetRegistry& AssetRegistry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>("AssetRegistry").Get();
		AssetRegistry.ScanFilesSynchronous(Saved, true);
		return bAll;
	}

	void Tick()
	{
		if (UWorldPartitionSubsystem* Subsystem = World->GetSubsystem<UWorldPartitionSubsystem>())
		{
			Subsystem->Tick(0.f);
		}
	}

	bool Build(FAutomationTestBase& Test)
	{
		Mount.Mount();

		UPackage* WorldPackage = CreatePackage(*(Mount.Root + Name));
		UWorld::InitializationValues IVS;
		IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
		World = UWorld::CreateWorld(EWorldType::Editor, false,
			FName(*Name), WorldPackage, true, ERHIFeatureLevel::Num, &IVS);
		if (!Test.TestNotNull(TEXT("fixture: a partitioned world in a package of its own"), World))
		{
			return false;
		}
		WorldPackage->SetPackageFlags(PKG_ContainsMap);
		World->SetFlags(RF_Public | RF_Standalone);
		WorldPartition = World->GetWorldPartition();
		if (!Test.TestNotNull(TEXT("fixture: the world is partitioned"), WorldPartition))
		{
			return false;
		}

		FMantlePlaceVaultManifest Manifest;
		FMantlePlaceImportResult Result;
		TArray<FString> Log;
		MantlePlaceImportTiming::FTimeline Timeline;
		Timeline.Start();
		const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
		FMantlePlaceImportContext Order(World, Manifest, nullptr, Selection, Timeline, Result, Log);
		Order.Identity = Identity;
		Order.ContentRoot = Mount.Root + TEXT("Content");

		ALandscape* Landscape = BuildFlatLandscape(World, UMaterial::GetDefaultMaterial(MD_Surface));
		if (!Test.TestNotNull(TEXT("fixture: the landscape is built"), Landscape)
			|| !Test.TestTrue(TEXT("fixture: the landscape splits"), MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize)))
		{
			return false;
		}
		Order.Family = EMantlePlaceLayerFamily::Terrain;
		Order.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(MantlePlaceImportNaming::EActorKind::Landscape, Identity));

		const FVector BuildingAt(3.5 * QuadsPerSection * 100.0, 3.5 * QuadsPerSection * 100.0, 0.0);
		AStaticMeshActor* Building = World->SpawnActor<AStaticMeshActor>(BuildingAt, FRotator::ZeroRotator);
		if (Building != nullptr)
		{
			Building->GetStaticMeshComponent()->SetStaticMesh(LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube")));
		}
		Order.Family = EMantlePlaceLayerFamily::Buildings;
		Order.Claim(Building, TEXT("MP_UnloadedTestBuilding"));
		if (!Test.TestTrue(TEXT("fixture: the building streams, as the HLOD families do"),
			Building != nullptr && Building->GetIsSpatiallyLoaded()))
		{
			return false;
		}

		LandscapeGuid = Landscape->GetActorGuid();
		BuildingGuid = Building->GetActorGuid();
		TArray<FGuid> ProxyGuids;
		if (ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
			{
				if (Proxy.IsValid())
				{
					ProxyGuids.Add(Proxy->GetActorGuid());
				}
			}
		}
		const int32 ExpectedProxies = MantlePlaceWorldPartitionLogic::ProxyCount(ComponentsPerAxis, ComponentsPerAxis, GridSize);
		if (!Test.TestEqual(TEXT("fixture: the split yields its proxies"), ProxyGuids.Num(), ExpectedProxies))
		{
			return false;
		}
		Landscape = nullptr;
		Building = nullptr;

		if (!Test.TestTrue(TEXT("fixture: the level and its actors save"), SaveAll()))
		{
			return false;
		}
		Tick();

		Unloaded = ProxyGuids;
		Unloaded.RemoveAt(0);
		Unloaded.Add(BuildingGuid);
		WorldPartition->UnpinActors(Unloaded);
		Tick();
		CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS, true);

		Test.TestEqual(TEXT("fixture: the loaded half is the parent and one proxy"),
			CountTaggedInLevel(World, Identity), 2);
		return Test.TestEqual(TEXT("fixture: the unloaded half is described, and only described"),
			CountUnloadedTaggedDescriptors(World, Identity), Unloaded.Num());
	}

	const FString Name;
	FMantlePlaceTestMount Mount;
	const FString Identity = TEXT("abababab-3390");
	UWorld* World = nullptr;
	UWorldPartition* WorldPartition = nullptr;
	FGuid LandscapeGuid;
	FGuid BuildingGuid;
	TArray<FGuid> Unloaded;
};

void ImportAgain(UWorld* World, const FString& Identity, const FString& ContentRoot)
{
	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
	FMantlePlaceImportContext Order(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Order.Identity = Identity;
	Order.ContentRoot = ContentRoot;
	Order.Family = EMantlePlaceLayerFamily::Terrain;
	ALandscape* Landscape = BuildFlatLandscape(World, UMaterial::GetDefaultMaterial(MD_Surface));
	MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize);
	Order.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(MantlePlaceImportNaming::EActorKind::Landscape, Identity));
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionUnloadedRoundTripTest,
	"MantlePlace.Import.WorldPartition.UnloadedRoundTrip",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionUnloadedRoundTripTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionUnloadedTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the unloaded round trip needs an editor world. Skipped."));
		return true;
	}

	FPartlyUnloadedLevel Level(TEXT("MantlePlaceUnloadedRoundTrip"));
	if (!Level.Build(*this))
	{
		return false;
	}
	UWorld* World = Level.World;
	const FString& Identity = Level.Identity;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();

	TestEqual(TEXT("v1's read refuses, counting the unloaded half"),
		MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(World, Identity),
		MantlePlaceWorldPartitionLogic::ExplainUnloadedPriorActors(Level.Unloaded.Num()));

	int32 Destroyed = 0;
	const FDelegateHandle DeletedHandle = GEngine->OnLevelActorDeleted().AddLambda([&Destroyed](AActor*) { ++Destroyed; });
	{
		MantlePlaceImportOrchestration::FPriorActorLoad Load;
		const FString Refusal = MantlePlaceImportOrchestration::LoadUnloadedPriorActors(World, Identity, Load);
		TestTrue(*FString::Printf(TEXT("every unloaded prior actor loads, so no refusal (got: %s)"), *Refusal), Refusal.IsEmpty());
		TestEqual(TEXT("the walk found the unloaded half"), Load.Unloaded, Level.Unloaded.Num());
		TestEqual(TEXT("and loaded every one"), Load.Loaded, Level.Unloaded.Num());
		TestEqual(TEXT("so the whole prior import is in the level"), CountTaggedInLevel(World, Identity), 2 + Level.Unloaded.Num());

		const int32 Removed = MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline);
		TestEqual(TEXT("the wipe removes the parent, every proxy and the building"), Removed, 2 + Level.Unloaded.Num());
	}
	GEngine->OnLevelActorDeleted().Remove(DeletedHandle);
	TestEqual(TEXT("and destroyed exactly those"), Destroyed, 2 + Level.Unloaded.Num());
	CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS, true);

	TestEqual(TEXT("zero orphans: no tagged actor is left in the level"), CountTaggedInLevel(World, Identity), 0);
	TestEqual(TEXT("no streaming proxy is left, tagged or not"), CountProxiesInLevel(World), 0);
	TestEqual(TEXT("zero orphans: no tagged actor is left unloaded"), CountUnloadedTaggedDescriptors(World, Identity), 0);
	TestTrue(TEXT("so v1's read finds nothing unloaded"),
		MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(World, Identity).IsEmpty());

	for (const TCHAR* Pass : { TEXT("re-import"), TEXT("re-import again") })
	{
		ImportAgain(World, Identity, Level.Mount.Root + TEXT("Content"));
		MantlePlaceImportOrchestration::FPriorActorLoad Load;
		const FString Refusal = MantlePlaceImportOrchestration::LoadUnloadedPriorActors(World, Identity, Load);
		TestTrue(*FString::Printf(TEXT("%s: the wipe's leftover descriptors are no refusal (got: %s)"), Pass, *Refusal), Refusal.IsEmpty());
		TestEqual(*FString::Printf(TEXT("%s: and nothing to load"), Pass), Load.Unloaded, 0);
		TestEqual(*FString::Printf(TEXT("%s: the wipe removes the landscape and its proxies"), Pass),
			MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline),
			1 + MantlePlaceWorldPartitionLogic::ProxyCount(ComponentsPerAxis, ComponentsPerAxis, GridSize));
		TestEqual(*FString::Printf(TEXT("%s: zero orphans"), Pass), CountTaggedInLevel(World, Identity), 0);
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionUnloadableFallbackTest,
	"MantlePlace.Import.WorldPartition.UnloadableFallback",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionUnloadableFallbackTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionUnloadedTest;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the unloadable fallback needs an editor world. Skipped."));
		return true;
	}

	FPartlyUnloadedLevel Level(TEXT("MantlePlaceUnloadableFallback"));
	if (!Level.Build(*this))
	{
		return false;
	}
	UWorld* World = Level.World;
	const FString& Identity = Level.Identity;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();

	const FWorldPartitionActorDescInstance* BuildingDesc = Level.WorldPartition->GetActorDescInstance(Level.BuildingGuid);
	if (!TestNotNull(TEXT("fixture: the building is described"), BuildingDesc))
	{
		return false;
	}
	const FString BuildingFile = FPackageName::LongPackageNameToFilename(
		BuildingDesc->GetActorPackage().ToString(), FPackageName::GetAssetPackageExtension());
	if (!TestTrue(TEXT("fixture: the building's package is deleted from disk"), IFileManager::Get().Delete(*BuildingFile)))
	{
		return false;
	}
	AddExpectedMessage(TEXT("Can't load actor guid"), EAutomationExpectedMessageFlags::Contains, 0);
	AddExpectedMessage(TEXT("could not be loaded; refused"), EAutomationExpectedMessageFlags::Contains, 1);

	int32 Destroyed = 0;
	const FDelegateHandle DeletedHandle = GEngine->OnLevelActorDeleted().AddLambda([&Destroyed](AActor*) { ++Destroyed; });
	MantlePlaceImportOrchestration::FPriorActorLoad Load;
	const FString Refusal = MantlePlaceImportOrchestration::LoadUnloadedPriorActors(World, Identity, Load);
	GEngine->OnLevelActorDeleted().Remove(DeletedHandle);

	TestEqual(TEXT("one the wipe cannot load refuses the re-import with v1's message"),
		Refusal, MantlePlaceWorldPartitionLogic::ExplainUnloadedPriorActors(1));
	TestEqual(TEXT("the walk found the unloaded half"), Load.Unloaded, Level.Unloaded.Num());
	TestEqual(TEXT("and loaded all but the building"), Load.Loaded, Level.Unloaded.Num() - 1);
	TestEqual(TEXT("then released what it loaded"), Load.References.Num(), 0);
	TestEqual(TEXT("nothing was destroyed"), Destroyed, 0);
	TestEqual(TEXT("nothing it loaded is left in the level: the loaded half is as it was"),
		CountTaggedInLevel(World, Identity), 2);
	CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS, true);
	TestEqual(TEXT("and the unloaded half is unloaded again"),
		CountUnloadedTaggedDescriptors(World, Identity), Level.Unloaded.Num());
	return true;
}

#endif
