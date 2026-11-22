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

namespace
{
UDataLayerAsset* FindAssetNamed(const FString& ContentRoot, const FString& FamilyName);
UDataLayerInstance* FindOrCreateNamed(UWorld* World, const FString& ContentRoot, const FString& FamilyName);
}

UDataLayerAsset* MantlePlaceDataLayers::FindAsset(const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	return FindAssetNamed(ContentRoot, MantlePlaceDataLayerLogic::FamilyName(Family));
}

namespace
{
UDataLayerAsset* FindAssetNamed(const FString& ContentRoot, const FString& FamilyName)
{
	if (FamilyName.IsEmpty())
	{
		return nullptr;
	}
	const FString AssetName = MantlePlaceImportNaming::DataLayerAssetName(FamilyName);
	const FString PackageName = MantlePlaceImportNaming::DataLayerPackageName(ResolvedRoot(ContentRoot), FamilyName);
	const FString ObjectPath = MantlePlaceImportNaming::ObjectPathOf(PackageName, AssetName);

	if (UDataLayerAsset* Loaded = FindObject<UDataLayerAsset>(nullptr, *ObjectPath))
	{
		return Loaded;
	}
	return Cast<UDataLayerAsset>(StaticLoadObject(
		UDataLayerAsset::StaticClass(), nullptr, *ObjectPath, nullptr, LOAD_NoWarn | LOAD_Quiet));
}
}

UDataLayerInstance* MantlePlaceDataLayers::Find(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	const AWorldDataLayers* WorldDataLayers = WorldDataLayersOf(World, false);
	const UDataLayerAsset* Asset = WorldDataLayers != nullptr ? FindAsset(ContentRoot, Family) : nullptr;
	return Asset != nullptr ? const_cast<UDataLayerInstance*>(WorldDataLayers->GetDataLayerInstance(Asset)) : nullptr;
}

UDataLayerInstance* MantlePlaceDataLayers::FindOrCreate(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	return FindOrCreateNamed(World, ContentRoot, MantlePlaceDataLayerLogic::FamilyName(Family));
}

namespace
{
UDataLayerInstance* FindOrCreateNamed(UWorld* World, const FString& ContentRoot, const FString& FamilyName)
{
	AWorldDataLayers* WorldDataLayers = FamilyName.IsEmpty() ? nullptr : WorldDataLayersOf(World, true);
	UDataLayerEditorSubsystem* Subsystem = UDataLayerEditorSubsystem::Get();
	if (WorldDataLayers == nullptr || Subsystem == nullptr)
	{
		return nullptr;
	}

	UDataLayerAsset* Asset = FindAssetNamed(ContentRoot, FamilyName);
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
		Asset->SetType(EDataLayerType::Runtime);
		FAssetRegistryModule::AssetCreated(Asset);
		Package->MarkPackageDirty();
	}

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
		Instance->SetInitialRuntimeState(EDataLayerRuntimeState::Activated);
	}
	return Instance;
}
}

UDataLayerInstance* MantlePlaceDataLayers::FindOdbl(UWorld* World, const FString& ContentRoot)
{
	const AWorldDataLayers* WorldDataLayers = WorldDataLayersOf(World, false);
	const UDataLayerAsset* Asset = WorldDataLayers != nullptr
		? FindAssetNamed(ContentRoot, MantlePlaceImportNaming::OdblLayerName()) : nullptr;
	return Asset != nullptr ? const_cast<UDataLayerInstance*>(WorldDataLayers->GetDataLayerInstance(Asset)) : nullptr;
}

bool MantlePlaceDataLayers::AssignOdbl(AActor* Actor, const FString& ContentRoot)
{
	UWorld* World = Actor != nullptr ? Actor->GetWorld() : nullptr;
	if (!UWorld::IsPartitionedWorld(World))
	{
		return false;
	}
	UDataLayerInstance* Instance = FindOrCreateNamed(World, ContentRoot, MantlePlaceImportNaming::OdblLayerName());
	UDataLayerEditorSubsystem* Subsystem = UDataLayerEditorSubsystem::Get();
	if (Instance != nullptr && Subsystem != nullptr && !Actor->ContainsDataLayer(Instance))
	{
		Subsystem->AddActorToDataLayer(Actor, Instance);
	}
	const bool bAssigned = Instance != nullptr && Actor->ContainsDataLayer(Instance);
	UE_CLOG(!bAssigned, LogMantlePlaceImport, Warning,
		TEXT("Could not assign %s to the ODbL Data Layer; it imports, but outside the layer."), *Actor->GetActorLabel());
	return bAssigned;
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
