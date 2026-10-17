// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceArtifactHandler.h"

DECLARE_LOG_CATEGORY_EXTERN(LogMantlePlaceImport, Log, All);

/**
 * What ImportVaultPackage orchestrates, one function per step, each testable without a bundle: the
 * integrity gate, the "is there anything to build" gate, the wipe of a prior import, and the walk of
 * the handlers. UMantlePlaceImporterLibrary::ImportVaultPackage is these in order; none of the
 * per-artifact work is here (that is the handlers'), and none of the decisions (that is the *Logic
 * units').
 */
namespace MantlePlaceImportOrchestration
{
using FHandlers = MantlePlaceArtifacts::FHandlers;

/**
 * Fail-closed integrity check: hash every payload the handlers declare against the digest the
 * manifest published for it, before anything is imported. A corrupt/truncated/tampered download
 * returns false here, creating nothing. WHICH payloads are in the chain is each handler's
 * CollectDeclared, walked here -- a payload left out of a hand-written chain was invisible once, and
 * the list is what makes "every declared payload is checked" an assertion rather than a claim.
 *
 * A payload with no published digest is skipped, not passed: "valid but unverified" is a third state,
 * and collapsing it into "verified" is a lie the log then tells the user (spec/format.md section 5).
 */
bool VerifyAll(
	const FMantlePlaceVaultManifest& Manifest, const FZipArchiveReader& Reader,
	MantlePlaceImportTiming::FTimeline& Timeline, FString& OutError);

/**
 * The refusal for a selection that asks for a terrain representation the bundle cannot produce, or
 * empty when the import can go ahead. Decided BEFORE the destructive wipe: otherwise a prior
 * import's still-referenced assets would be force-deleted to produce nothing.
 *
 * A selection that asks for no terrain representation at all (buildings only, say) is not refused:
 * it asked for nothing the bundle could fail to have.
 */
FString ExplainNothingToImport(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest,
	const FMantlePlaceImportSelection& Selection);

/**
 * Idempotent re-import, assets: wipe any prior content for THIS order so reimported assets land on
 * clean names. Guarded by the provenance record, because the folder is named by the TRUNCATED
 * identity and the delete is a force-delete. Returns the refusal to show the user (and wipes
 * nothing) when the folder is another order's, or legacy content; empty when it is clear to import.
 */
FString WipePriorAssets(
	const FString& Identity, const FString& DestPackagePath, MantlePlaceImportTiming::FTimeline& Timeline);

/**
 * The second half of the asset wipe, which WipePriorAssets runs after its delete: anything the
 * delete deleted but could not free, because something still held it, is moved out of the folder's
 * paths into the transient package. Left there, the re-import would import into it and keep its
 * cleared flags, and the level could no longer save the actor that references it (dcc #349).
 * Returns how many it moved; 0 when the delete freed everything.
 */
int32 RetireDeletedLeftovers(const FString& DestPackagePath);

/**
 * Idempotent re-import, actors: remove the actors a PRIOR import of this same ORDER spawned, so
 * re-importing replaces them instead of stacking duplicate (coincident) actors. Matched on the
 * import TAG, which carries the full identity and survives being renamed or dragged elsewhere --
 * never on the label, which a user edits. Returns how many were removed; a second call returns 0.
 *
 * A tagged landscape's streaming proxies go with it, tagged or not, and before it: proxies first,
 * the parent landscape last.
 *
 * A prior foliage scatter's PCG partition actors are emptied, and counted among the replaced. Given
 * OutKeptPartitionActors, the ones left holding nothing are KEPT there for the import that follows
 * to generate on, and ReleaseKeptPartitionActors destroys the rest after it: in a saved World
 * Partition level, a destroyed partition actor's cell stays recorded with the PCG subsystem until the
 * next save, and the subsystem neither creates nor generates on a recorded cell with no loaded actor,
 * so destroying them first loses the re-import's scatter (dcc #347). Without it, a wipe that imports
 * nothing in its place, they are destroyed here.
 */
int32 ReplacePriorActors(UWorld* World, const FString& Identity, MantlePlaceImportTiming::FTimeline& Timeline,
	TArray<TWeakObjectPtr<AActor>>* OutKeptPartitionActors = nullptr);

/**
 * After the import that followed ReplacePriorActors: destroy the kept partition actors it did not
 * generate on, so a re-import that leaves the scatter out, or covers fewer cells, leaves no empty
 * partition actor behind. Returns how many it destroyed.
 */
int32 ReleaseKeptPartitionActors(UWorld* World, const TArray<TWeakObjectPtr<AActor>>& KeptPartitionActors);

/**
 * The refusal for a re-import into a World Partition level when any actor of the prior import of
 * this ORDER is in an unloaded region, or empty when the import can go ahead (always, in a
 * non-partitioned level). ReplacePriorActors can only destroy what is loaded, so going ahead would
 * leave the unloaded ones behind as orphans beside the new import. Called before either wipe, so a
 * refusal changes nothing. A prior landscape's streaming proxies count whether or not they carry
 * the tag, matched by the landscape's GUID. So does a PCG partition actor whose cell overlaps a
 * prior foliage scatter's volume (MantlePlaceWorldPartitionLogic::CellHoldsPriorScatter): it holds
 * that scatter's generated instances untagged, and the wipe's cleanup reaches loaded ones only.
 * A descriptor an earlier wipe this session left behind does not count: the actor is destroyed,
 * not unloaded, and the next save deletes the descriptor
 * (MantlePlaceWorldPartitionLogic::IsDestroyedThisSession).
 */
FString ExplainUnloadedPriorActors(UWorld* World, const FString& Identity);

/** Each included handler's Prepare / Import / Finalize, in registry order. */
void RunPrepare(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
void RunImport(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
void RunFinalize(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
}
