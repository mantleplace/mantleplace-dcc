// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceHLODs.h"

#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformTime.h"
#include "Misc/PackageName.h"
#include "UObject/GCObjectScopeGuard.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"
#include "WorldPartition/HLOD/HLODActor.h"
#include "WorldPartition/HLOD/HLODLayer.h"
#include "WorldPartition/RuntimeHashSet/RuntimePartition.h"
#include "WorldPartition/RuntimeHashSet/WorldPartitionRuntimeHashSet.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionRuntimeHash.h"
#include "WorldPartition/WorldPartitionHLODsBuilder.h"

namespace
{
/**
 * Make the level's World Partition stream this HLOD layer. Since 5.8 an HLOD layer counts only when
 * a runtime partition's HLOD setup lists it; an actor in any other layer is built into no HLOD at
 * all. The layer joins the main partition's first HLOD setup, the one its default HLOD layer uses,
 * and a level with none gets one, made from its main partition as the engine makes the default.
 * True when the level streams the layer afterwards.
 */
bool RegisterInWorld(UWorld* World, UHLODLayer* Layer)
{
	UWorldPartition* WorldPartition = World != nullptr ? World->GetWorldPartition() : nullptr;
	UWorldPartitionRuntimeHash* Hash = WorldPartition != nullptr ? WorldPartition->RuntimeHash.Get() : nullptr;
	if (Hash == nullptr || Layer == nullptr)
	{
		return false;
	}
	const FSoftObjectPath LayerPath(Layer);
	if (Hash->IsValidHLODLayer(NAME_None, LayerPath))
	{
		return true;
	}

	// The partitions are the World Settings' own property, private to the hash; edit it as the
	// Details panel does, through reflection.
	UWorldPartitionRuntimeHashSet* HashSet = Cast<UWorldPartitionRuntimeHashSet>(Hash);
	const FArrayProperty* PartitionsProperty = HashSet != nullptr
		? FindFProperty<FArrayProperty>(UWorldPartitionRuntimeHashSet::StaticClass(), TEXT("RuntimePartitions"))
		: nullptr;
	TArray<FRuntimePartitionDesc>* Partitions = PartitionsProperty != nullptr
		? PartitionsProperty->ContainerPtrToValuePtr<TArray<FRuntimePartitionDesc>>(HashSet)
		: nullptr;
	if (Partitions == nullptr || Partitions->IsEmpty() || (*Partitions)[0].MainLayer == nullptr)
	{
		return false;
	}

	HashSet->Modify();
	FRuntimePartitionDesc& Main = (*Partitions)[0];
	if (Main.HLODSetups.IsEmpty())
	{
		URuntimePartition* HLODPartition = DuplicateObject<URuntimePartition>(Main.MainLayer, HashSet);
		HLODPartition->Name = TEXT("MantlePlaceHLOD0");
		HLODPartition->LoadingRange = Main.MainLayer->LoadingRange * 2;
		HLODPartition->HLODIndex = 0;
		HLODPartition->bBlockOnSlowStreaming = false;
		HLODPartition->bClientOnlyVisible = true;
		HLODPartition->Priority = 0;
		FRuntimePartitionHLODSetup& Setup = Main.HLODSetups.AddDefaulted_GetRef();
		Setup.Name = HLODPartition->Name;
		Setup.PartitionLayer = HLODPartition;
	}
	Main.HLODSetups[0].HLODLayers.AddUnique(Layer);
	return Hash->IsValidHLODLayer(NAME_None, LayerPath);
}
}

UHLODLayer* MantlePlaceHLODs::FindLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	if (!MantlePlaceHLODLogic::HasHLOD(Family))
	{
		return nullptr;
	}
	const FString FamilyName = MantlePlaceDataLayerLogic::FamilyName(Family);
	const FString AssetName = MantlePlaceImportNaming::HLODLayerAssetName(FamilyName);
	const FString PackageName = MantlePlaceImportNaming::HLODLayerPackageName(
		MantlePlaceImportNaming::ResolveContentRoot(ContentRoot), FamilyName);
	const FString ObjectPath = MantlePlaceImportNaming::ObjectPathOf(PackageName, AssetName);

	// In memory first: an earlier import this session made it and nobody has saved it yet.
	if (UHLODLayer* Loaded = FindObject<UHLODLayer>(nullptr, *ObjectPath))
	{
		return Loaded;
	}
	return Cast<UHLODLayer>(StaticLoadObject(
		UHLODLayer::StaticClass(), nullptr, *ObjectPath, nullptr, LOAD_NoWarn | LOAD_Quiet));
}

UHLODLayer* MantlePlaceHLODs::FindOrCreateLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	if (!MantlePlaceHLODLogic::HasHLOD(Family))
	{
		return nullptr;
	}
	if (UHLODLayer* Existing = FindLayer(ContentRoot, Family))
	{
		return Existing;
	}

	const FString FamilyName = MantlePlaceDataLayerLogic::FamilyName(Family);
	UPackage* Package = CreatePackage(*MantlePlaceImportNaming::HLODLayerPackageName(
		MantlePlaceImportNaming::ResolveContentRoot(ContentRoot), FamilyName));
	if (Package == nullptr)
	{
		return nullptr;
	}
	UHLODLayer* Layer = NewObject<UHLODLayer>(Package,
		FName(*MantlePlaceImportNaming::HLODLayerAssetName(FamilyName)), RF_Public | RF_Standalone | RF_Transactional);
	if (Layer == nullptr)
	{
		return nullptr;
	}
	Layer->SetLayerType(MantlePlaceHLODLogic::LayerType(Family));

	// The builder settings follow the layer type; the layer makes them when its type is edited, so
	// announce the edit exactly as the Details panel does. LayerType is private, hence the name.
	if (FProperty* TypeProperty = FindFProperty<FProperty>(UHLODLayer::StaticClass(), TEXT("LayerType")))
	{
		FPropertyChangedEvent TypeChanged(TypeProperty, EPropertyChangeType::ValueSet);
		// Through UObject: the override is private to UHLODLayer, the virtual is public.
		static_cast<UObject*>(Layer)->PostEditChangeProperty(TypeChanged);
	}

	FAssetRegistryModule::AssetCreated(Layer);
	Package->MarkPackageDirty();
	return Layer;
}

bool MantlePlaceHLODs::Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family)
{
	UWorld* World = Actor != nullptr ? Actor->GetWorld() : nullptr;
	if (!MantlePlaceHLODLogic::ShouldAssign(UWorld::IsPartitionedWorld(World), Family))
	{
		return false;
	}
	UHLODLayer* Layer = FindOrCreateLayer(ContentRoot, Family);
	UE_CLOG(Layer != nullptr && !RegisterInWorld(World, Layer), LogMantlePlaceImport, Warning,
		TEXT("The level's World Partition does not stream the %s HLOD layer, so it builds no HLOD for it; add it "
			 "under World Settings > Runtime Partitions > HLOD Partitions."),
		*Layer->GetName());
	if (Layer != nullptr && Actor->GetHLODLayer() != Layer)
	{
		Actor->Modify();
		Actor->SetHLODLayer(Layer);
	}
	const bool bAssigned = Layer != nullptr && Actor->GetHLODLayer() == Layer;
	UE_CLOG(!bAssigned, LogMantlePlaceImport, Warning,
		TEXT("Could not put %s in the %s HLOD layer; it imports, but builds no HLOD."),
		*Actor->GetActorLabel(), *MantlePlaceDataLayerLogic::FamilyName(Family));
	return bAssigned;
}

int32 MantlePlaceHLODs::CountAssigned(UWorld* World, const FString& ContentRoot)
{
	if (World == nullptr)
	{
		return 0;
	}
	TSet<const UHLODLayer*> Layers;
	for (const EMantlePlaceLayerFamily Family : MantlePlaceHLODLogic::HLODFamilies())
	{
		if (const UHLODLayer* Layer = FindLayer(ContentRoot, Family))
		{
			Layers.Add(Layer);
		}
	}
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += Layers.Contains(It->GetHLODLayer()) ? 1 : 0;
	}
	return Count;
}

int32 MantlePlaceHLODs::CountHLODActors(UWorld* World)
{
	int32 Count = 0;
	if (World != nullptr)
	{
		for (TActorIterator<AWorldPartitionHLOD> It(World); It; ++It)
		{
			++Count;
		}
	}
	return Count;
}

MantlePlaceHLODs::FBuildReport MantlePlaceHLODs::Build(UWorld* World)
{
	FBuildReport Report;

	// What the engine's build would silently leave out: every actor with an HLOD layer whose package
	// is unsaved, the same test the editor's own Build HLODs applies.
	const bool bPartitioned = UWorld::IsPartitionedWorld(World);
	bool bWorldSaved = false;
	int32 Unsaved = 0;
	if (bPartitioned)
	{
		const UPackage* WorldPackage = World->GetPackage();
		bWorldSaved = WorldPackage != nullptr && !WorldPackage->HasAnyPackageFlags(PKG_NewlyCreated)
			&& FPackageName::DoesPackageExist(WorldPackage->GetName());
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			const UPackage* Package = It->GetPackage();
			Unsaved += (!It->IsA<AWorldPartitionHLOD>() && It->GetHLODLayer() != nullptr
				&& Package != nullptr && Package->IsDirty()) ? 1 : 0;
		}
	}
	const FString Refusal = MantlePlaceHLODLogic::ExplainBuildRefusal(bPartitioned, bWorldSaved, Unsaved);
	if (!Refusal.IsEmpty())
	{
		Report.Message = Refusal;
		UE_LOG(LogMantlePlaceImport, Warning, TEXT("HLOD build refused: %s"), *Refusal);
		return Report;
	}

	// The engine's builder, as its in-editor Build HLODs runs it, without the dialogs: it saves
	// nothing, and the HLOD actors it makes or rebuilds are left for the user's save.
	UWorldPartitionHLODsBuilder* Builder = NewObject<UWorldPartitionHLODsBuilder>(GetTransientPackage());
	Builder->SetUseSlowTask(!IsRunningCommandlet() && !FApp::IsUnattended());
	Builder->SetSaveActors(false);
	Builder->SetConsiderUnsavedHLODActors(true);

	const double Start = FPlatformTime::Seconds();
	{
		FGCObjectScopeGuard BuilderGuard(Builder);
		Report.bBuilt = Builder->RunBuilder(World);
	}
	Report.Seconds = FPlatformTime::Seconds() - Start;
	Report.HLODActors = CountHLODActors(World);
	Report.Message = Report.bBuilt
		? MantlePlaceHLODLogic::DescribeBuildCost(Report.HLODActors, Report.Seconds)
		: FString::Printf(TEXT("The HLOD build failed after %.1f s; the Output Log's LogWorldPartition lines say why."),
			Report.Seconds);
	UE_LOG(LogMantlePlaceImport, Display, TEXT("%s"), *Report.Message);
	return Report;
}
