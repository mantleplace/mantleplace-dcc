// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

/** One payload the integrity pre-check considers, and the digest the manifest published for it. */
struct FMantlePlaceDeclaredArtifact
{
	/** In-zip path, resolved from this host's own pointer block (HPS-33). */
	FString Path;

	/** Lowercase-hex sha256, or empty when the manifest publishes none for this payload. */
	FString Sha256;

	/** True when a digest was published, i.e. when this payload can be reported *verified* rather
	 *  than *valid but unverified*. Never "corrupt": a missing optional hash is unknown, not wrong
	 *  (spec/format.md section 5). */
	bool IsVerifiable() const { return !Sha256.IsEmpty(); }
};

/**
 * What one archive says it holds (MPB 1.10.0): the manifest's `view` block and, from `files`, the part
 * each file belongs to. Read for ONE purpose -- telling a payload the archive was cut without (a
 * download selection) from one it should hold and does not (corruption). No placement value comes
 * from it.
 */
struct FMantlePlaceArchiveView
{
	/** A `view` block with a `parts` array was stated. Without one the archive is taken to hold
	 *  everything the manifest declares, as it always was. */
	bool bDeclared = false;

	/** `view.kind` verbatim: `everything`, `selection`, `host`, or one this reader has not met (which
	 *  means the archive holds exactly `parts`). */
	FString Kind;

	/** `view.parts`: the parts this archive holds. Compared whole, never parsed beyond that. */
	TArray<FString> Parts;

	/** `files[<archive path>].part`, for each file the manifest lists with a part. */
	TMap<FString, FString> FileParts;
};

/**
 * Pure (engine-/IO-free) logic for the import's fail-closed integrity pre-check: WHICH payloads a
 * bundle declares, and which of them the manifest published a digest for.
 *
 * The decision lives here rather than in the importer because it is the thing that goes wrong
 * silently. The chain used to be a hand-written boolean expression at the call site, and a payload
 * left out of it was invisible: `Landcover/TreePoints.csv` was the one artifact the importer
 * brought in unverified, while the manifest had been shipping a digest for it. A list a headless
 * test can walk is what makes "every declared payload is checked" an assertion instead of a claim.
 */
struct FMantlePlaceIntegrityLogic
{
	/**
	 * Every payload this bundle declares, in the order the pre-check reads them: the registry's
	 * (MantlePlaceArtifacts::MakeAll), each handler contributing its own. The drape and the heightmap
	 * come first (the two the schema makes hash-required), then the layers, then the optional geometry.
	 *
	 * Payloads with no published digest are INCLUDED, carrying an empty Sha256. They are what
	 * "valid but unverified" is reported from, and dropping them here would make the two states
	 * indistinguishable from the caller.
	 */
	static TArray<FMantlePlaceDeclaredArtifact> CollectDeclaredArtifacts(
	    const FMantlePlaceVaultManifest& Manifest);

	/** The subset of CollectDeclaredArtifacts the pre-check can actually verify (a digest was
	 *  published), as paths. The importer's log line and the conformance corpus both read this,
	 *  so "what was verified" is one answer rather than two. */
	static TArray<FString> VerifiablePaths(const FMantlePlaceVaultManifest& Manifest);

	/**
	 * True when the archive's own `view` says it was cut WITHOUT this file: a declared payload is not an
	 * error to be missing when the curator's selection left it out (spec/format.md: "not selected" is a
	 * reason distinct from corruption). The archive's `view` decides, not the zip listing alone:
	 *
	 *  - The file's part (from `files`) is not in `view.parts`: left out, so skipped and named.
	 *  - Its part IS in `view.parts`: the archive claims to hold it, so a missing entry is corruption.
	 *    False here, and the pre-check keeps refusing.
	 *  - `view.kind` is `everything`, there is no `view`, no `files` mapping, or the file is not listed
	 *    in it: there is no way to tell a selection from corruption, so false (fail closed).
	 */
	static bool LeavesOut(const FMantlePlaceArchiveView& View, const FString& Path);
	static bool LeavesOut(const FMantlePlaceVaultManifest& Manifest, const FString& Path);

	/** True when there is at least one path and the archive leaves out every one: the artifact those
	 *  payloads make has nothing in this archive to import, so it is skipped rather than failed. */
	static bool LeavesOutAll(const FMantlePlaceVaultManifest& Manifest, const TArray<FString>& Paths);

	/**
	 * True when this archive holds the Cesium quantized-mesh tileset (`terrain.cesium`): the manifest
	 * points at it (`layout.cesium_terrain`) AND the archive was not cut without it. A manifest describes
	 * the whole bundle, so its pointer alone does not say what one archive holds; the `view` does. With no
	 * `view`, or an `everything` one, the pointer decides (nothing was cut); otherwise (a selection, a
	 * host cut, a kind this reader has not met) the archive holds exactly `view.parts`, and the part's
	 * token (the id up to the `@`, compared whole) must be among them.
	 *
	 * Read to word a refusal truthfully ("use Stream into Cesium" only when there is a tileset to stream).
	 */
	static bool HoldsCesiumTerrain(const FMantlePlaceVaultManifest& Manifest);
};
