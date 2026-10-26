// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * The decisions an import into a World Partition level makes, as pure functions. The importer
 * executes them (MantlePlaceLandscapeImporter::SplitForWorldPartition, the context's Claim, the
 * orchestration's wipe); this unit decides, and MantlePlace.Import.WorldPartition.Logic asserts it.
 *
 * Nothing here runs in a non-partitioned level: every caller is behind the world's own
 * partitioned check, so an import into a non-partitioned level behaves exactly as it did before.
 */
namespace MantlePlaceWorldPartitionLogic
{
/**
 * How many ALandscapeStreamingProxy actors the engine's split yields: the component grid divided
 * by the grid size, per axis, rounded up (a partial cell is still a proxy). Zero for no components
 * or an unusable grid size.
 */
int32 ProxyCount(int32 ComponentsX, int32 ComponentsY, int32 GridSizeInComponents);

/** Where an actor falls in the wipe's destroy order. */
enum class EWipeRank : uint8
{
	/** An ALandscapeStreamingProxy: goes first, while its parent still holds the landscape info. */
	StreamingProxy,
	/** Everything else the import spawned. */
	Other,
	/** The ALandscape a set of proxies belongs to: goes last, after every one of them. */
	LandscapeParent,
};

/**
 * The order to destroy actors in, as indices into Ranks: proxies first, the landscape parent
 * last, the relative order within a rank kept. Destroying the parent first leaves its proxies
 * pointing at a landscape info that no longer has an owner, which is the engine's own refusal
 * (ULandscapeInfo::CanDeleteLandscape) the other way round.
 */
TArray<int32> WipeOrder(const TArray<EWipeRank>& Ranks);

/**
 * The unloaded-actor policy: in a partitioned level every actor the importer spawns is kept always
 * loaded, except the streamed families. A landscape: the parent ALandscape is non-spatially loaded by
 * its own constructor, and its streaming proxies have to stay spatially loaded for the level to stream
 * at all. The HLOD families (MantlePlaceHLODLogic::HasHLOD): an always-loaded actor is never
 * represented by an HLOD. A re-import loads the streamed ones that are unloaded before its wipe
 * (MantlePlaceImportOrchestration::LoadUnloadedPriorActors), so the wipe never leaves one behind.
 *
 * Kept for the rest even though the wipe now loads them too (dcc 339): the importer's other readers of
 * a prior import walk loaded actors only. Prepare for handover finds the ODbL actors it deletes and the
 * credits it keeps by iterating the level, and the wipe finds a prior foliage scatter's footprint, the
 * one thing that ties an untagged PCG partition actor to the order, from its loaded volume. A streamed
 * road or scatter volume would be missed by both whenever its cell is unloaded.
 */
bool ShouldForceNonSpatiallyLoaded(bool bPartitioned, bool bStreamedFamily);

/**
 * The refusal for a re-import whose prior actors include some in unloaded World Partition regions,
 * or empty when there are none: v1's whole policy, and since v2 the text of its fallback
 * (ExplainUnloadablePriorActors). A wipe can only destroy what is loaded, so going ahead would leave
 * those actors behind as orphans beside the new import; refusing changes nothing.
 */
FString ExplainUnloadedPriorActors(int32 UnloadedCount);

/**
 * v2: the refusal for a re-import whose wipe loaded LoadedCount of the UnloadedCount prior actors it
 * found in unloaded regions, or empty when it loaded every one. One it could not load refuses the
 * whole re-import with v1's message, counting the ones it could not load: the wipe is never partial,
 * and the caller releases what it loaded, so a refusal changes nothing.
 */
FString ExplainUnloadablePriorActors(int32 UnloadedCount, int32 LoadedCount);

/**
 * Whether an unloaded PCG partition actor, by its cell's footprint, may hold instances a prior
 * foliage scatter generated: the cell overlaps one of the prior scatter volumes' footprints by a
 * positive area. A partition actor carries no import tag and is shared by every partitioned
 * component on its cell, so the footprint is all that ties it to the order; a wipe cannot clean an
 * unloaded one, so it counts towards the refusal. A cell that only touches a volume's edge holds
 * none of it, and a cell with no bounds cannot be placed, so neither counts.
 */
bool CellHoldsPriorScatter(const FBox2D& Cell, const TArray<FBox2D>& ScatterFootprints);

/** What memory holds of the package behind an actor descriptor that reads as unloaded. */
struct FDescriptorPackage
{
	/** The package is in memory: its actor was loaded at some point this session. */
	bool bInMemory = false;
	/** The package has unsaved changes. */
	bool bDirty = false;
	/** The package still holds a live actor, one not destroyed. */
	bool bHoldsLiveActor = false;
};

/**
 * Whether an actor descriptor that reads as unloaded belongs to an actor this session destroyed:
 * its package is in memory, dirty, and holds no live actor, so the next save deletes the package
 * and the descriptor with it. Until that save the descriptor outlives the actor, and counting it
 * would refuse a re-import while every region is loaded. An actor in a region that is really
 * unloaded has no package in memory, or an unchanged one, so it still counts.
 */
bool IsDestroyedThisSession(const FDescriptorPackage& Package);
}
