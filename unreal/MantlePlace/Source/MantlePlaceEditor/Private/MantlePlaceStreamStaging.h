// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * What the streaming path remembers about a bundle it has already laid out on disk, so a second
 * stream of the same bundle can serve what is there instead of extracting and rewriting it again.
 *
 * "Stream into Cesium" extracts the bundle's Cesium-ready subtree into the project's `Saved`
 * directory and then rewrites the extracted `layer.json` availability. Both were unconditional: every
 * stream re-extracted every tile and rescanned the whole tile tree, however many times the same
 * bundle was streamed in one session.
 *
 * Two things make the reuse safe to key, and both are recorded rather than assumed:
 *
 *  - **The identity, in full.** The staging directory is named by it, and unlike the content folder
 *    it is not truncated. Truncation is a readability choice for a folder a user looks at; nothing
 *    reads this one, and the truncation's collision — two identities sharing eight characters — is
 *    sharper here than in the content tree, because the availability rewrite declares *every*
 *    `.terrain` file it finds beneath the directory. A colliding bundle's leftover tiles would be
 *    declared available and served as this bundle's.
 *  - **The manifest digest.** The identity is the ORDER, which is stable across rebuilds — that is
 *    the whole point of ADR 0002 — so identity alone would happily reuse a previous build's tiles
 *    for a re-materialized order. The digest changes when the bundle does, so it is what says
 *    "the same bundle", where the identity says "the same order".
 *
 * The record lives beside the staging directory rather than inside it, because the directory is
 * served over HTTP and a bookkeeping file inside it would be servable content.
 *
 * Pure decision, headless-tested: `MantlePlace.Import.StreamStaging`.
 */
namespace MantlePlaceStreamStaging
{
	/**
	 * The staging layout a record was written by. Bumped when the layout changes meaning, so a build
	 * that does not know a layout re-stages rather than serving something it cannot predict.
	 *
	 * 1 = full-identity directory under Saved/MantlePlace/StreamStaging, Cesium terrain subtree plus
	 *     Imagery/, availability rewritten in place.
	 */
	constexpr int32 CurrentSchemeVersion = 1;

	struct FRecord
	{
		/** The FULL import identity of the bundle staged there. */
		FString Identity;

		/** sha256 of that bundle's manifest — what distinguishes two builds of one order. */
		FString ManifestSha256;

		/** The zip-entry prefix the terrain subtree was extracted from (it is renamed across bundle
		 *  versions), so a bundle that moved its terrain folder re-stages. */
		FString TerrainPrefix;

		/** The manifest's own pointer to the rewritten layer.json, relative to the staging root. */
		FString CesiumTerrainPath;

		/** How many zip entries the extraction wrote. Zero is not a staged bundle. */
		int32 EntryCount = 0;

		/** The layout that wrote it. */
		int32 SchemeVersion = 0;
	};

	/** Whether the staged copy can be served as-is, or has to be laid out again. */
	enum class EVerdict : uint8
	{
		/** Extract the subtree and rewrite availability. The staging directory is cleared first. */
		Stage,

		/** Everything needed is already on disk for this exact bundle. Serve it. */
		Reuse,
	};

	/**
	 * The whole decision, as a pure function of what was found. Anything that does not match exactly
	 * re-stages: the staging directory is scratch, so rebuilding it costs an extraction and risks
	 * nothing, while serving a stale one silently serves the wrong terrain.
	 *
	 * `bTerrainRootPresent` is the caller's answer to "is the rewritten layer.json still on disk" —
	 * a user who cleaned their `Saved` directory leaves a record behind, and a record is not content.
	 */
	EVerdict Classify(
		bool bRecordFound,
		const FRecord& Found,
		const FRecord& Incoming,
		bool bTerrainRootPresent);

	// --- Serialization (pure) -------------------------------------------------------------------

	FString ToJson(const FRecord& Record);
	bool FromJson(const FString& Json, FRecord& OutRecord);

	// --- Storage --------------------------------------------------------------------------------

	/** Where a bundle's Cesium-ready subtree is laid out for the local server to host. */
	FString StagingDir(const FString& Identity);

	/** Where that directory's record lives — beside it, never inside it: the directory is served. */
	FString RecordPath(const FString& Identity);

	bool Read(const FString& Identity, FRecord& OutRecord);
	bool Write(const FRecord& Record);
}
