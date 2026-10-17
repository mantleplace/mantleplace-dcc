// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceDataLayers.h"

#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "DataLayer/DataLayerEditorSubsystem.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "UObject/Package.h"
#include "WorldPartition/DataLayer/DataLayerAsset.h"
#include "WorldPartition/DataLayer/DataLayerInstance.h"
#include "WorldPartition/DataLayer/WorldDataLayers.h"

namespace
{
FString ResolvedRoot(const FString& ContentRoot)
{
	return MantlePlaceImportNaming::ResolveContentRoot(ContentRoot);
}

AWorldDataLayers* WorldDataLayersOf(UWorld* World, bool bCreate)
{
	if (World == nullptr || !UWorld::IsPartitionedWorld(World))
	{
		return nullptr;
	}
	AWorldDataLayers* WorldDataLayers = World->GetWorldDataLayers();
	if (WorldDataLayers == nullptr && bCreate)
	{
		WorldDataLayers = AWorldDataLayers::Create(World);
	}
	return WorldDataLayers;
}
}

UDataLayerAsset* MantlePlaceDataLayers::FindAsset(const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	const FString FamilyName = MantlePlaceDataLayerLogic::FamilyName(Family);
	if (FamilyName.IsEmpty())
	{
		return nullptr;
	}
	const FString AssetName = MantlePlaceImportNaming::DataLayerAssetName(FamilyName);
	const FString PackageName = MantlePlaceImportNaming::DataLayerPackageName(ResolvedRoot(ContentRoot), FamilyName);
	const FString ObjectPath = MantlePlaceImportNaming::ObjectPathOf(PackageName, AssetName);

	// In memory first: an earlier import this session made it and nobody has saved it yet.
	if (UDataLayerAsset* Loaded = FindObject<UDataLayerAsset>(nullptr, *ObjectPath))
	{
		return Loaded;
	}
	return Cast<UDataLayerAsset>(StaticLoadObject(
		UDataLayerAsset::StaticClass(), nullptr, *ObjectPath, nullptr, LOAD_NoWarn | LOAD_Quiet));
}

UDataLayerInstance* MantlePlaceDataLayers::Find(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	const AWorldDataLayers* WorldDataLayers = WorldDataLayersOf(World, /*bCreate*/ false);
	const UDataLayerAsset* Asset = WorldDataLayers != nullptr ? FindAsset(ContentRoot, Family) : nullptr;
	return Asset != nullptr ? const_cast<UDataLayerInstance*>(WorldDataLayers->GetDataLayerInstance(Asset)) : nullptr;
}

UDataLayerInstance* MantlePlaceDataLayers::FindOrCreate(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	const FString FamilyName = MantlePlaceDataLayerLogic::FamilyName(Family);
	AWorldDataLayers* WorldDataLayers = FamilyName.IsEmpty() ? nullptr : WorldDataLayersOf(World, /*bCreate*/ true);
	UDataLayerEditorSubsystem* Subsystem = UDataLayerEditorSubsystem::Get();
	if (WorldDataLayers == nullptr || Subsystem == nullptr)
	{
		return nullptr;
	}

	// The shared asset: reused when any import (this session or a saved one) already made it.
	UDataLayerAsset* Asset = FindAsset(ContentRoot, Family);
	if (Asset == nullptr)
	{
		const FString AssetName = MantlePlaceImportNaming::DataLayerAssetName(FamilyName);
		UPackage* Package = CreatePackage(
			*MantlePlaceImportNaming::DataLayerPackageName(ResolvedRoot(ContentRoot), FamilyName));
		if (Package == nullptr)
		{
			return nullptr;
		}
		Asset = NewObject<UDataLayerAsset>(Package, FName(*AssetName), RF_Public | RF_Standalone | RF_Transactional);
		if (Asset == nullptr)
		{
			return nullptr;
		}
		// Runtime, so a game can stream a family as well as the editor hide it.
		Asset->SetType(EDataLayerType::Runtime);
		FAssetRegistryModule::AssetCreated(Asset);
		Package->MarkPackageDirty();
	}

	// The world's instance of it: one per world, made once.
	if (const UDataLayerInstance* Existing = WorldDataLayers->GetDataLayerInstance(Asset))
	{
		return const_cast<UDataLayerInstance*>(Existing);
	}
	FDataLayerCreationParameters Parameters;
	Parameters.DataLayerAsset = Asset;
	Parameters.WorldDataLayers = WorldDataLayers;
	UDataLayerInstance* Instance = Subsystem->CreateDataLayerInstance(Parameters);
	if (Instance != nullptr && Instance->IsRuntime())
	{
		// A Runtime layer starts Unloaded unless told otherwise, which would make an import vanish
		// from every game run. Activated keeps it exactly as it was before layers existed.
		Instance->SetInitialRuntimeState(EDataLayerRuntimeState::Activated);
	}
	return Instance;
}

bool MantlePlaceDataLayers::Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	UWorld* World = Actor != nullptr ? Actor->GetWorld() : nullptr;
	if (!MantlePlaceDataLayerLogic::ShouldAssign(UWorld::IsPartitionedWorld(World), Family))
	{
		return false;
	}
	UDataLayerInstance* Instance = FindOrCreate(World, ContentRoot, Family);
	UDataLayerEditorSubsystem* Subsystem = UDataLayerEditorSubsystem::Get();
	if (Instance != nullptr && Subsystem != nullptr && !Actor->ContainsDataLayer(Instance))
	{
		Subsystem->AddActorToDataLayer(Actor, Instance);
	}
	const bool bAssigned = Instance != nullptr && Actor->ContainsDataLayer(Instance);
	UE_CLOG(!bAssigned, LogMantlePlaceImport, Warning,
		TEXT("Could not assign %s to the %s Data Layer; it imports, but outside the layer."),
		*Actor->GetActorLabel(), *MantlePlaceDataLayerLogic::FamilyName(Family));
	return bAssigned;
}
