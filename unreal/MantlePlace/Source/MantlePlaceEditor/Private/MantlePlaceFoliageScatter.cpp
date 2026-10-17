// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceFoliageScatter.h"

#include "MantlePlaceFoliageVolume.h"
#include "MantlePlaceTreeScatterSettings.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/BoxComponent.h"
#include "Elements/IO/PCGDataTableElement.h"
#include "Elements/PCGStaticMeshSpawner.h"
#include "Engine/DataTable.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "Grid/PCGPartitionActor.h"
#include "MeshSelectors/PCGMeshSelectorByAttribute.h"
#include "PCGCommon.h"
#include "PCGComponent.h"
#include "PCGGraph.h"
#include "Subsystems/PCGSubsystem.h"
#include "UObject/Package.h"

namespace MantlePlaceFoliageScatter
{
UPCGGraph* BuildGraph(const FString& PackagePath, const FString& AssetName, const FInputs& Inputs, FString& OutError)
{
	if (Inputs.TreePoints == nullptr)
	{
		OutError = TEXT("no tree-points table to bind");
		return nullptr;
	}
	UPackage* Package = CreatePackage(*(PackagePath / AssetName));
	UPCGGraph* Graph = NewObject<UPCGGraph>(Package, FName(*AssetName), RF_Public | RF_Standalone | RF_Transactional);

	// 1. The table, one point per row, its columns as attributes under their contract names.
	UPCGLoadDataTableSettings* LoadSettings = nullptr;
	UPCGNode* Load = Graph->AddNodeOfType(LoadSettings);
	LoadSettings->DataTable = Inputs.TreePoints;
	LoadSettings->bSynchronousLoad = true;

	// 2. Ours: binds the columns, applies the rasters, chooses a mesh and a transform per point.
	UMantlePlaceTreeScatterSettings* ScatterSettings = nullptr;
	UPCGNode* Scatter = Graph->AddNodeOfType(ScatterSettings);
	ScatterSettings->CanopyHeight = Inputs.CanopyHeight;
	ScatterSettings->WaterMask = Inputs.WaterMask;
	ScatterSettings->LandCover = Inputs.LandCover;
	ScatterSettings->AoiSizeCm = Inputs.AoiSizeCm;
	for (const FSoftObjectPath& Path : Inputs.TreeMeshes)
	{
		ScatterSettings->TreeMeshes.Add(TSoftObjectPtr<UStaticMesh>(Path));
	}
	for (const FSoftObjectPath& Path : Inputs.ShrubMeshes)
	{
		ScatterSettings->ShrubMeshes.Add(TSoftObjectPtr<UStaticMesh>(Path));
	}

	// 3. The engine's spawner, selecting each point's mesh by the attribute the scatter wrote.
	UPCGStaticMeshSpawnerSettings* SpawnerSettings = nullptr;
	UPCGNode* Spawner = Graph->AddNodeOfType(SpawnerSettings);
	SpawnerSettings->SetMeshSelectorType(UPCGMeshSelectorByAttribute::StaticClass());
	if (UPCGMeshSelectorByAttribute* Selector = Cast<UPCGMeshSelectorByAttribute>(SpawnerSettings->MeshSelectorParameters))
	{
		Selector->AttributeName = ScatterSettings->MeshAttribute;
	}
	SpawnerSettings->bSynchronousLoad = true;

#if WITH_EDITOR
	Load->SetNodePosition(0, 0);
	Scatter->SetNodePosition(400, 0);
	Spawner->SetNodePosition(800, 0);
#endif

	if (Graph->AddEdge(Load, PCGPinConstants::DefaultOutputLabel, Scatter, PCGPinConstants::DefaultInputLabel) == nullptr
		|| Graph->AddEdge(Scatter, PCGPinConstants::DefaultOutputLabel, Spawner, PCGPinConstants::DefaultInputLabel) == nullptr)
	{
		OutError = TEXT("the graph's nodes did not connect");
		return nullptr;
	}

	FAssetRegistryModule::AssetCreated(Graph);
	Graph->MarkPackageDirty();
	return Graph;
}

AMantlePlaceFoliageVolume* SpawnVolume(UWorld* World, const FInputs& Inputs)
{
	if (World == nullptr)
	{
		return nullptr;
	}
	// The ground's range with a margin above for the canopy and below for the trace; an unknown
	// range takes a kilometre each way, which only the ground trace ever reads.
	constexpr double MarginCm = 10000.0;
	const bool bKnownRange = Inputs.GroundMaxZCm > Inputs.GroundMinZCm;
	const double MinZ = bKnownRange ? Inputs.GroundMinZCm - MarginCm : -100000.0;
	const double MaxZ = bKnownRange ? Inputs.GroundMaxZCm + MarginCm : 100000.0;

	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	AMantlePlaceFoliageVolume* Volume = World->SpawnActor<AMantlePlaceFoliageVolume>(
		FVector(0.0, 0.0, (MinZ + MaxZ) * 0.5), FRotator::ZeroRotator, Params);
	if (Volume == nullptr)
	{
		return nullptr;
	}
	Volume->Bounds->SetBoxExtent(FVector(Inputs.AoiSizeCm.X * 0.5, Inputs.AoiSizeCm.Y * 0.5, (MaxZ - MinZ) * 0.5));
	return Volume;
}

void Generate(AMantlePlaceFoliageVolume* Volume, UPCGGraph* Graph)
{
	if (Volume == nullptr || Volume->Pcg == nullptr || Graph == nullptr)
	{
		return;
	}
	// Partitioned in a World Partition level, so foliage generates and streams per cell; a
	// non-partitioned level generates on the volume, which the same graph does unchanged.
	Volume->Pcg->SetIsPartitioned(UWorld::IsPartitionedWorld(Volume->GetWorld()));
	Volume->Pcg->SetGraph(Graph);
	Volume->Pcg->GenerateLocal(/*bForce*/ true);
}

TArray<AActor*> CleanupGenerated(UWorld* World, const TArray<AActor*>& Actors)
{
	TArray<AActor*> PartitionActors;
	UPCGSubsystem* Subsystem = UPCGSubsystem::GetInstance(World);
	for (AActor* Actor : Actors)
	{
		const AMantlePlaceFoliageVolume* Volume = Cast<AMantlePlaceFoliageVolume>(Actor);
		UPCGComponent* Component = Volume != nullptr ? Volume->Pcg.Get() : nullptr;
		if (Component == nullptr)
		{
			continue;
		}
		if (Subsystem != nullptr)
		{
			for (const TObjectPtr<APCGPartitionActor>& PartitionActor : Subsystem->GetPCGComponentPartitionActorMappings(Component))
			{
				if (PartitionActor != nullptr)
				{
					PartitionActors.AddUnique(PartitionActor.Get());
				}
			}
		}
		// Now, not deferred: the volume is destroyed next, and a deferred cleanup would run on a
		// component that no longer exists, leaving its instances on the partition actors.
		Component->CleanupLocalImmediate(/*bRemoveComponents*/ true, /*bCleanupLocalComponents*/ true);
		for (AActor* PartitionActor : PartitionActors)
		{
			Cast<APCGPartitionActor>(PartitionActor)->RemoveGraphInstance(Component);
		}
	}
	return PartitionActors;
}

int32 DestroyEmptyPartitionActors(UWorld* World, const TArray<AActor*>& PartitionActors)
{
	int32 Destroyed = 0;
	for (AActor* Actor : PartitionActors)
	{
		APCGPartitionActor* PartitionActor = Cast<APCGPartitionActor>(Actor);
		if (!IsValid(PartitionActor) || PartitionActor->HasLocalPCGComponents() || PartitionActor->HasGraphInstances())
		{
			continue; // another component still generates on this cell
		}
		World->EditorDestroyActor(PartitionActor, /*bShouldModifyLevel*/ true);
		++Destroyed;
	}
	return Destroyed;
}
}
