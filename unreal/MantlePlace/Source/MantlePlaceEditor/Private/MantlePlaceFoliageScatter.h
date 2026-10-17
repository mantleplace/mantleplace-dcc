// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class AActor;
class AMantlePlaceFoliageVolume;
class UDataTable;
class UPCGGraph;
class UTexture2D;
class UWorld;

/**
 * The PCG foliage scatter's impure half: the graph it builds, the volume it spawns, the generation it
 * starts, and the sweep a wipe runs so nothing it generated outlives its order. What a point does is
 * MantlePlaceFoliageScatterLogic's, run by the graph's own node (UMantlePlaceTreeScatterSettings).
 *
 * The graph is authored here, in code, and built into the order's folder at import, rather than
 * shipped as a binary asset: it is three nodes -- Load Data Table, the Mantle Place Tree Scatter,
 * a Static Mesh Spawner selecting by the `Mesh` attribute -- so a diff reviews it whole, and every
 * order's graph carries that order's own table and rasters.
 */
namespace MantlePlaceFoliageScatter
{
struct FInputs
{
	UDataTable* TreePoints = nullptr;
	UTexture2D* CanopyHeight = nullptr;
	UTexture2D* WaterMask = nullptr;
	UTexture2D* LandCover = nullptr;
	/** The AOI's span, (North, East) in cm, centred on the origin. */
	FVector2D AoiSizeCm = FVector2D::ZeroVector;
	/** The ground's elevation range in cm; the volume spans it with a margin. Equal means unknown. */
	double GroundMinZCm = 0.0;
	double GroundMaxZCm = 0.0;
	TArray<FSoftObjectPath> TreeMeshes;
	TArray<FSoftObjectPath> ShrubMeshes;
};

/** Build the foliage graph as `<PackagePath>/<AssetName>`, created and marked dirty, never saved. */
UPCGGraph* BuildGraph(const FString& PackagePath, const FString& AssetName, const FInputs& Inputs, FString& OutError);

/**
 * Spawn the volume over the AOI, with no graph yet. The caller claims it before Generate: a
 * partitioned component makes its partition actors as soon as it has a graph, each taking the
 * volume's Data Layers as they are then, so the layer must be set first.
 */
AMantlePlaceFoliageVolume* SpawnVolume(UWorld* World, const FInputs& Inputs);

/**
 * Assign the graph, partitioned on the PCG grid when the world is partitioned and generating on the
 * volume itself when it is not, and start generation. Asynchronous: the PCG subsystem completes it
 * over the editor's next ticks.
 */
void Generate(AMantlePlaceFoliageVolume* Volume, UPCGGraph* Graph);

/**
 * Before a wipe destroys `Actors`: clean up what every foliage volume among them generated, and
 * return the PCG partition actors that held it. They are shared by every partitioned component on
 * the same cell, so they are destroyed only once empty (DestroyEmptyPartitionActors).
 */
TArray<AActor*> CleanupGenerated(UWorld* World, const TArray<AActor*>& Actors);

/** Destroy the partition actors among these that no longer hold anything. Returns how many. */
int32 DestroyEmptyPartitionActors(UWorld* World, const TArray<AActor*>& PartitionActors);
}
