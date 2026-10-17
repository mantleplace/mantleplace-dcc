// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"
#include "WorldPartition/HLOD/HLODLayer.h"

/**
 * The decisions behind World Partition HLODs for imported content, as pure functions.
 * MantlePlaceHLODs executes them; MantlePlace.Import.HLOD.Logic asserts them.
 *
 * Which families: Terrain (the landscape's streaming proxies, and the terrain mesh) and Buildings,
 * the two whose silhouette is what a distant view is made of. Roads, foliage, coverage and records
 * get no HLOD layer: they read as noise at the distance an HLOD stands in for them.
 *
 * Scoping: like the Data Layers, an HLOD layer is per family and shared across every import in the
 * project, so it lives beside them under <ContentRoot>/Layers, outside every order's folder.
 *
 * Build: prompted, never run by the import. The engine's HLOD build reads the level's saved actor
 * descriptors and leaves out every actor whose package is unsaved; the importer saves nothing, so a
 * build run straight after an import would include none of the imported actors. Its measured cost
 * (unreal/docs/generated-content.md) is a second reason: six and a half minutes on a reference bundle
 * whose import takes 25 s.
 * The import says how to build in its log; the explicit step, MantlePlaceHLODs::Build, refuses an
 * unsaved level and reports what the build cost.
 */
namespace MantlePlaceHLODLogic
{
/** The families whose actors are given an HLOD layer, in a fixed order. */
TArray<EMantlePlaceLayerFamily> HLODFamilies();

/** Whether a family's actors are given an HLOD layer. */
bool HasHLOD(EMantlePlaceLayerFamily Family);

/**
 * Whether an actor claimed for this family is given its family's HLOD layer: only in a World
 * Partition level (a non-partitioned level has no HLODs, and its import is unchanged), and only for
 * an HLOD family.
 */
bool ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family);

/**
 * The HLOD layer type a family's layer is created with. Simplified mesh for both: a bundle's terrain
 * and buildings are a few large unique meshes, which instancing and merging do not reduce at all.
 * A landscape's components build through the landscape's own HLOD builder whatever the type.
 */
EHLODLayerType LayerType(EMantlePlaceLayerFamily Family);

/**
 * Why an HLOD build cannot run yet, or empty when it can. Refused outside World Partition, for a
 * level that has never been saved, and while any actor that has an HLOD layer is unsaved: the
 * engine's build reads the saved level and would leave those actors out without a word.
 */
FString ExplainBuildRefusal(bool bPartitioned, bool bWorldSaved, int32 UnsavedHLODSourceActors);

/** The import log's line for a finished build: how many HLOD actors, and what the build cost. */
FString DescribeBuildCost(int32 HLODActors, double Seconds);

/**
 * The import log's note on HLODs, or empty when there is nothing to build: outside World Partition,
 * or when the import gave no actor an HLOD layer. Names both ways to build.
 */
FString PostImportNote(bool bPartitioned, int32 ActorsWithHLODLayer);

/** The documented offline invocation's arguments for one map, after the editor and project paths. */
FString CommandletArguments(const FString& MapPackageName);
}
