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
 * The v1 unloaded-actor policy: in a partitioned level every actor the importer spawns is kept
 * always loaded, so the tag-matched wipe always sees it. The streamed families are the exception.
 * A landscape: the parent ALandscape is non-spatially loaded by its own constructor, and its
 * streaming proxies have to stay spatially loaded for the level to stream at all. The HLOD families
 * (MantlePlaceHLODLogic::HasHLOD): an always-loaded actor is never represented by an HLOD. A
 * re-import while any of them is unloaded is refused (ExplainUnloadedPriorActors), so the wipe
 * still never leaves one behind.
 */
bool ShouldForceNonSpatiallyLoaded(bool bPartitioned, bool bStreamedFamily);

/**
 * The refusal for a re-import whose prior actors include some in unloaded World Partition regions,
 * or empty when there are none. A wipe can only destroy what is loaded, so going ahead would leave
 * those actors behind as orphans beside the new import; refusing changes nothing.
 */
FString ExplainUnloadedPriorActors(int32 UnloadedCount);

/**
 * Whether an unloaded PCG partition actor, by its cell's footprint, may hold instances a prior
 * foliage scatter generated: the cell overlaps one of the prior scatter volumes' footprints by a
 * positive area. A partition actor carries no import tag and is shared by every partitioned
 * component on its cell, so the footprint is all that ties it to the order; a wipe cannot clean an
 * unloaded one, so it counts towards the refusal. A cell that only touches a volume's edge holds
 * none of it, and a cell with no bounds cannot be placed, so neither counts.
 */
bool CellHoldsPriorScatter(const FBox2D& Cell, const TArray<FBox2D>& ScatterFootprints);
}
