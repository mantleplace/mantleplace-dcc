// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"

class AActor;
class UDataLayerAsset;
class UDataLayerInstance;
class UWorld;

/**
 * World Partition Data Layers for imported actors: one per layer family, shared across every order
 * imported into the project (MantlePlaceDataLayerLogic records the scoping and why). This unit
 * executes; the decisions are MantlePlaceDataLayerLogic's, and the import context's Claim is the one
 * caller in the importer.
 *
 * Like everything the importer generates, the Data Layer asset is created in memory and its package
 * only marked dirty, never saved: the user's save takes it with the level that references it.
 */
namespace MantlePlaceDataLayers
{
/** The family's Data Layer asset when it already exists, loaded or on disk; null otherwise. Creates nothing. */
UDataLayerAsset* FindAsset(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/**
 * The family's Data Layer instance in this world, creating the shared asset and the world's instance
 * of it when either is missing and reusing them when not. A new layer is a Runtime layer whose
 * initial state is Activated, so a game run loads and shows the import exactly as it did before
 * layers existed. Null outside World Partition, for None, or when the engine refuses.
 */
UDataLayerInstance* FindOrCreate(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/**
 * The ODbL Data Layer: one shared layer, `DL_ODbL`, that content imported from `database` files joins
 * beside its family's. It is the second container ODbL content lands in (the first is the outliner
 * folder), and the one a game or a streaming setup can switch off whole.
 */
UDataLayerInstance* FindOdbl(UWorld* World, const FString& ContentRoot);

/** Assign one actor to the ODbL Data Layer. No-op returning false outside World Partition. */
bool AssignOdbl(AActor* Actor, const FString& ContentRoot);

/** The family's instance in this world when it exists; null otherwise. Creates nothing. */
UDataLayerInstance* Find(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/**
 * Assign one actor to its family's Data Layer, through the editor's own Data Layer subsystem so the
 * outliner and the actor's editor visibility follow. True when the actor is in the layer afterwards;
 * false (and a warning logged) when the engine refuses it. A no-op returning false outside World
 * Partition, which is what keeps a non-partitioned import unchanged.
 */
bool Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family);
}
