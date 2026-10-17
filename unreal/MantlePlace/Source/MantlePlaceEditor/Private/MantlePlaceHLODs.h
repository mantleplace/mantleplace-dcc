// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"

class AActor;
class UHLODLayer;
class UWorld;

/**
 * World Partition HLODs for imported actors: one HLOD layer per HLOD family, shared across every
 * order imported into the project, and the explicit build step. This unit executes; the decisions,
 * including why the import never builds, are MantlePlaceHLODLogic's. The import context's Claim is
 * the one caller of Assign in the importer.
 *
 * Like everything the importer generates, the HLOD layer asset is created in memory and its package
 * only marked dirty, never saved; the build saves nothing either, its HLOD actors left for the
 * user's save.
 */
namespace MantlePlaceHLODs
{
/** The family's HLOD layer asset when it already exists, loaded or on disk; null otherwise. Creates nothing. */
UHLODLayer* FindLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/**
 * The family's HLOD layer asset, creating it when missing and reusing it when not. Null for a family
 * with no HLOD (MantlePlaceHLODLogic::HasHLOD) or when the engine refuses.
 */
UHLODLayer* FindOrCreateLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/**
 * Put one actor in its family's HLOD layer. True when the actor has the layer afterwards. A no-op
 * returning false outside World Partition and for a family with no HLOD, which keeps a
 * non-partitioned import, and every other family, unchanged.
 */
bool Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

/** How many actors in this world are in one of the HLOD layers under this content root. */
int32 CountAssigned(UWorld* World, const FString& ContentRoot);

/** How many HLOD actors (AWorldPartitionHLOD) this world holds. */
int32 CountHLODActors(UWorld* World);

/** What an explicit build did: built or refused, the message for the log, and what it cost. */
struct FBuildReport
{
	bool bBuilt = false;
	FString Message;
	int32 HLODActors = 0;
	double Seconds = 0.0;
};

/**
 * The explicit post-import step: build the World Partition HLODs of a saved level, in this editor,
 * with the engine's own HLOD builder, and report the cost to the import log (LogMantlePlaceImport).
 * Refuses, building nothing, when MantlePlaceHLODLogic::ExplainBuildRefusal says so. Opens no dialog,
 * so it runs unattended.
 */
FBuildReport Build(UWorld* World);
}
