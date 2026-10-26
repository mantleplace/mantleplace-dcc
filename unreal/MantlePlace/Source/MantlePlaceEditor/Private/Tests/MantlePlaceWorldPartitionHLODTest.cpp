// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceHLODs.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLandscapeImporter.h"
#include "MantlePlaceWorldPartitionLogic.h"
#include "MantlePlaceTestMount.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/PrimitiveComponent.h"
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
#include "UObject/UObjectIterator.h"
#include "WorldPartition/HLOD/HLODActor.h"
#include "WorldPartition/HLOD/HLODLayer.h"
#include "WorldPartition/HLOD/HLODSourceActors.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionRuntimeHash.h"

// The HLOD round trip, without a bundle: a split landscape (Terrain), a building (Buildings) and a
// road (Roads), each claimed by the import context's own Claim under its family in a partitioned
// world, then the explicit build step run on it before and after a save.
//
// What it pins:
//  - Terrain's proxies and the building are in their family's shared HLOD layer; the road is in none;
//  - the HLOD layers are simplified-mesh layers, reused rather than duplicated by a second claim;
//  - the building streams, so an HLOD can stand in for it; the road stays always loaded (v1);
//  - the build refuses an unsaved level, building nothing, rather than silently leaving the import out;
//  - after a save, the build makes HLOD actors for Terrain and for Buildings, each with geometry over
//    the import, which is what renders in place of the unloaded cells at distance, and reports its cost;
//  - a wipe takes the order's actors out of the HLOD layers and leaves the layers intact.
//
// The saved level lives under /MantlePlace/, so the plugin's asset domain covers it, with its files in
// Intermediate, never in any Content folder (FMantlePlaceTestMount), and is deleted at the end.

namespace MantlePlaceWorldPartitionHLODTest
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
	Landscape->Import(FGuid::NewGuid(), 0, 0, Size - 1, Size - 1, /*NumSubsections*/ 1, QuadsPerSection,
		HeightDataPerLayers, TEXT(""), MaterialLayerDataPerLayers, ELandscapeImportAlphamapType::Additive,
		TArrayView<const FLandscapeLayer>());
	return Landscape;
}

AStaticMeshActor* SpawnBox(UWorld* World, const FVector& Location)
{
	AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(Location, FRotator::ZeroRotator);
	UStaticMesh* Cube = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
	if (Actor != nullptr && Cube != nullptr)
	{
		Actor->GetStaticMeshComponent()->SetStaticMesh(Cube);
		Actor->SetActorScale3D(FVector(4.0, 4.0, 8.0));
	}
	return Actor;
}

/** Save every package under the mount points that is dirty, the world's first. False on any failure. */
bool SaveMounted(UWorld* World, const FMantlePlaceTestMount& Mount, TArray<FString>& OutFailed)
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
	for (UPackage* Package : Packages)
	{
		const bool bMap = Package == World->GetPackage();
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
			OutFailed.Add(Package->GetName());
		}
	}
	// The level reads its actors back through the asset registry: let it see what was just written.
	IAssetRegistry& AssetRegistry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>("AssetRegistry").Get();
	AssetRegistry.ScanFilesSynchronous(Saved, /*bForceRescan*/ true);
	return OutFailed.Num() == 0;
}

const UHLODLayer* SourceLayerOf(const AWorldPartitionHLOD* HLOD)
{
	const UWorldPartitionHLODSourceActors* Sources = HLOD != nullptr ? HLOD->GetSourceActors() : nullptr;
	return Sources != nullptr ? Sources->GetHLODLayer() : nullptr;
}

int32 CountPrimitives(const AActor* Actor)
{
	int32 Count = 0;
	if (Actor != nullptr)
	{
		Actor->ForEachComponent<UPrimitiveComponent>(false, [&Count](const UPrimitiveComponent* Component)
		{
			Count += (Component->IsVisible() && Component->Bounds.SphereRadius > 0.0) ? 1 : 0;
		});
	}
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionHLODTest,
	"MantlePlace.Import.WorldPartition.HLODs",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionHLODTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionHLODTest;
	using Family = EMantlePlaceLayerFamily;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the HLOD round trip needs an editor world. Skipped."));
		return true;
	}

	// Under the plugin's root, its files in Intermediate, so the saved level never touches any Content.
	FMantlePlaceTestMount Mount(TEXT("MantlePlaceHLODTest"));
	Mount.Mount();

	UPackage* WorldPackage = CreatePackage(*(Mount.Root + TEXT("HLODTestMap")));
	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("HLODTestMap")), WorldPackage, /*bAddToRoot*/ true, ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a partitioned world in a package of its own"), World))
	{
		return false;
	}
	// As a map the editor creates: a map package, its world the asset saved in it.
	WorldPackage->SetPackageFlags(PKG_ContainsMap);
	World->SetFlags(RF_Public | RF_Standalone);

	const FString ContentRoot = Mount.Root + TEXT("Content");
	const int32 ExpectedProxies = MantlePlaceWorldPartitionLogic::ProxyCount(ComponentsPerAxis, ComponentsPerAxis, GridSize);

	FMantlePlaceVaultManifest Manifest;
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::Everything();
	FMantlePlaceImportContext Order(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Order.Identity = TEXT("eeeeeeee-5555");
	Order.ContentRoot = ContentRoot;

	// --- Import: terrain, a building over it, and a road, each under its handler's family ----------
	Order.Family = Family::Terrain;
	ALandscape* Landscape = BuildFlatLandscape(World, UMaterial::GetDefaultMaterial(MD_Surface));
	MantlePlaceLandscapeImporter::SplitForWorldPartition(Landscape, GridSize);
	Order.Claim(Landscape, MantlePlaceImportNaming::ActorLabel(MantlePlaceImportNaming::EActorKind::Landscape, Order.Identity));
	Order.Family = Family::Buildings;
	AStaticMeshActor* Building = SpawnBox(World, FVector(1400.0, 1400.0, 400.0));
	Order.Claim(Building, TEXT("MP_HLODTestBuilding"));
	Order.Family = Family::Roads;
	AStaticMeshActor* Road = SpawnBox(World, FVector(600.0, 600.0, 100.0));
	Order.Claim(Road, TEXT("MP_HLODTestRoad"));

	// --- Configured: the families' shared HLOD layers, and nothing else ------------------------------
	UHLODLayer* TerrainLayer = MantlePlaceHLODs::FindLayer(ContentRoot, Family::Terrain);
	UHLODLayer* BuildingsLayer = MantlePlaceHLODs::FindLayer(ContentRoot, Family::Buildings);
	TestNotNull(TEXT("the import made the Terrain HLOD layer"), TerrainLayer);
	TestNotNull(TEXT("the import made the Buildings HLOD layer"), BuildingsLayer);
	TestNull(TEXT("no HLOD layer for Roads"), MantlePlaceHLODs::FindLayer(ContentRoot, Family::Roads));
	for (const UHLODLayer* Layer : { TerrainLayer, BuildingsLayer })
	{
		TestTrue(TEXT("an HLOD layer is a simplified-mesh layer"), Layer != nullptr && Layer->GetLayerType() == EHLODLayerType::MeshSimplify);
		TestTrue(TEXT("whose builder settings follow its type"), Layer != nullptr && Layer->GetHLODBuilderSettings() != nullptr);
	}
	const UWorldPartitionRuntimeHash* Hash = World->GetWorldPartition() != nullptr ? World->GetWorldPartition()->RuntimeHash.Get() : nullptr;
	for (const UHLODLayer* Layer : { TerrainLayer, BuildingsLayer })
	{
		TestTrue(TEXT("the level's World Partition streams the HLOD layer, so it builds"),
			Hash != nullptr && Layer != nullptr && Hash->IsValidHLODLayer(NAME_None, FSoftObjectPath(Layer)));
	}
	int32 ProxiesInTerrain = 0;
	if (ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr)
	{
		for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
		{
			ProxiesInTerrain += (Proxy.IsValid() && Proxy->GetHLODLayer() == TerrainLayer) ? 1 : 0;
		}
	}
	TestEqual(TEXT("every landscape proxy is in the Terrain HLOD layer"), ProxiesInTerrain, ExpectedProxies);
	TestTrue(TEXT("the building is in the Buildings HLOD layer"), Building->GetHLODLayer() == BuildingsLayer);
	TestNull(TEXT("the road is in no HLOD layer"), Road->GetHLODLayer());
	TestTrue(TEXT("the building streams, so an HLOD can stand in for it"), Building->GetIsSpatiallyLoaded());
	TestFalse(TEXT("the road stays always loaded (v1)"), Road->GetIsSpatiallyLoaded());
	TestTrue(TEXT("a second claim reuses the shared layer"),
		MantlePlaceHLODs::FindOrCreateLayer(ContentRoot, Family::Buildings) == BuildingsLayer);
	TestEqual(TEXT("the import counts its HLOD-layered actors"),
		MantlePlaceHLODs::CountAssigned(World, ContentRoot), 1 + ExpectedProxies + 1);
	TestTrue(TEXT("the actors live in packages of their own, as World Partition saves them"), Building->IsPackageExternal());
	TestTrue(TEXT("under the test's mount points, so they save to Intermediate"), Mount.Owns(Building->GetPackage()->GetName()));

	// --- Build before a save: refused, nothing built --------------------------------------------------
	const MantlePlaceHLODs::FBuildReport Refused = MantlePlaceHLODs::Build(World);
	TestFalse(TEXT("unsaved: the build is refused"), Refused.bBuilt);
	TestTrue(TEXT("unsaved: and says to save first"), Refused.Message.Contains(TEXT("File > Save All")));
	TestEqual(TEXT("unsaved: no HLOD actor"), MantlePlaceHLODs::CountHLODActors(World), 0);

	// --- Save, then build: HLOD actors for Terrain and for Buildings, with geometry -------------------
	TArray<FString> Failed;
	TestTrue(TEXT("the level and its actors save"), SaveMounted(World, Mount, Failed));
	for (const FString& Package : Failed)
	{
		AddError(FString::Printf(TEXT("could not save %s"), *Package));
	}
	const MantlePlaceHLODs::FBuildReport Built = MantlePlaceHLODs::Build(World);
	AddInfo(Built.Message);
	TestTrue(TEXT("saved: the build runs"), Built.bBuilt);
	TestTrue(TEXT("saved: the log reports the cost"), Built.Message.StartsWith(TEXT("HLODs built: ")));

	// What the import covers: every actor it put in an HLOD layer, the proxies and the building.
	FBox ImportBounds(ForceInit);
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (!It->IsA<AWorldPartitionHLOD>() && It->GetHLODLayer() != nullptr)
		{
			ImportBounds += It->GetComponentsBoundingBox(/*bNonColliding*/ true);
		}
	}
	int32 TerrainHLODs = 0;
	int32 BuildingsHLODs = 0;
	int32 WithGeometry = 0;
	int32 OverImport = 0;
	for (TActorIterator<AWorldPartitionHLOD> It(World); It; ++It)
	{
		const UHLODLayer* Source = SourceLayerOf(*It);
		TerrainHLODs += Source == TerrainLayer ? 1 : 0;
		BuildingsHLODs += Source == BuildingsLayer ? 1 : 0;
		WithGeometry += CountPrimitives(*It) > 0 ? 1 : 0;
		OverImport += It->GetHLODBounds().IsValid && It->GetHLODBounds().Intersect(ImportBounds.ExpandBy(1000.0)) ? 1 : 0;
	}
	TestEqual(TEXT("saved: the report counts the HLOD actors"), Built.HLODActors, MantlePlaceHLODs::CountHLODActors(World));
	TestTrue(TEXT("saved: Terrain has an HLOD"), TerrainHLODs > 0);
	TestTrue(TEXT("saved: Buildings have an HLOD"), BuildingsHLODs > 0);
	TestEqual(TEXT("saved: every HLOD actor has geometry to render at distance"), WithGeometry, Built.HLODActors);
	TestEqual(TEXT("saved: every HLOD actor stands over the import"), OverImport, Built.HLODActors);

	// --- Wipe: out of the HLOD layers, the layers intact ---------------------------------------------
	MantlePlaceImportOrchestration::ReplacePriorActors(World, Order.Identity, Timeline);
	int32 StillLayered = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		StillLayered += (!It->IsA<AWorldPartitionHLOD>() && It->GetHLODLayer() != nullptr) ? 1 : 0;
	}
	TestEqual(TEXT("wipe: no source actor is left in an HLOD layer"), StillLayered, 0);
	TestTrue(TEXT("wipe: the Terrain HLOD layer is intact"), MantlePlaceHLODs::FindLayer(ContentRoot, Family::Terrain) == TerrainLayer);
	TestTrue(TEXT("wipe: the Buildings HLOD layer is intact"), MantlePlaceHLODs::FindLayer(ContentRoot, Family::Buildings) == BuildingsLayer);

	// Nothing of the test outlives it: no dirty package offered for saving, no file, no mount.
	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	World->RemoveFromRoot();
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
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
