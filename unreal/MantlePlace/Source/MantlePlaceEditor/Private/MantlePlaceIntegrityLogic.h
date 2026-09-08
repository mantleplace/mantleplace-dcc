// Copyright Mantle Place. All Rights Reserved.

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
	 * Every payload this bundle declares, in the order the pre-check reads them: the heightmap and
	 * the drape first (the two the schema makes hash-required), then the optional geometry, then
	 * the landscape layers with their UE-ready companions.
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
};
