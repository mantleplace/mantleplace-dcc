// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;

/**
 * Pure decisions for streaming an MPB 3.0 order into Cesium. A 3.x bundle holds no terrain tiles: they are
 * the terrain tiles Extra the platform lists for the order, a free download beside the bundle that carries no
 * version promise (spec/format.md 9.9, 12). Streaming serves those tiles as it serves a 2.x bundle's own.
 *
 *  - **Where the tiles live.** Beside the bundle's zip, as `TerrainTiles.zip` in the same folder: in the
 *    vault cache that is the order's own folder, so a second ask finds the file the first one fetched and
 *    fetches nothing. A bundle zip that came from somewhere else has no such file beside it unless the
 *    curator put one there, and the plugin never writes into a folder it did not make.
 *  - **Where a tileset is.** Found by its `layer.json` inside the Extra's zip, never by a folder name the
 *    Extra carries no promise of: the shallowest `layer.json`, in a folder (a `layer.json` at the root of the
 *    zip would make every entry of it a tile), and the tiles are what lies under that folder.
 *  - **What happens when there are none.** The platform lists none for the order, or the zip on disk has none
 *    beside it: the action says why and does nothing else. It never falls back to another source.
 *
 * The 2.x path does not come through here: a 2.x bundle holds its own tiles and streams as it always has.
 * Headless-tested: `MantlePlace.Import.TerrainTiles`.
 */
struct FMantlePlaceTerrainTilesLogic
{
	/** The file the Extra is kept in, beside the bundle's zip. */
	static const TCHAR* TilesFileName() { return TEXT("TerrainTiles.zip"); }

	/** Where the Extra is kept for a bundle at BundleZipPath: the same folder, whatever that zip is called. */
	static FString TilesZipBeside(const FString& BundleZipPath);

	/** True when BundleZipPath lies inside CacheRoot (the vault cache): a bundle the plugin itself downloaded. */
	static bool IsInVaultCache(const FString& BundleZipPath, const FString& CacheRoot);

	/** The tileset an Extra's zip holds. */
	struct FTileset
	{
		bool bFound = false;

		/** The entry that is the tileset's `layer.json`, as the zip names it. */
		FString LayerJsonPath;

		/** The folder it sits in, with its trailing slash: every entry under it is the tileset's. */
		FString Prefix;

		/** How many `.terrain` entries lie under Prefix. */
		int32 TileCount = 0;
	};

	/** Find the tileset among a zip's entry names. Nothing found when none has a `layer.json` inside a folder. */
	static FTileset FindTileset(const TArray<FString>& EntryNames);

	/** Where a bundle's terrain comes from for this ask. */
	enum class ESource : uint8
	{
		/** A 2.x bundle: its own tiles, as before. */
		Bundled,

		/** A 3.x bundle with its tiles beside it: serve those. */
		ExtraBeside,

		/** A 3.x bundle with no usable tiles beside it. */
		ExtraMissing,
	};

	static ESource DecideSource(bool bFormatThree, bool bTilesBesideUsable);

	/** What fetching the Extra comes to once the vault listing is in. */
	enum class EFetchPlan : uint8
	{
		/** Usable tiles are already beside the bundle: fetch nothing. */
		Reuse,

		/** The platform lists the tiles: mint a link and download them. */
		Fetch,

		/** The platform lists no terrain tiles for the order (or no such order): the action is disabled. */
		NoTilesListed,
	};

	/** `Item` is the listing row of the bundle's order, or null when the listing holds no such order. */
	static EFetchPlan PlanFetch(bool bTilesBesideUsable, const FMantlePlaceVaultItem* Item);

	/** True when a download of `ActualBytes` is the size the platform stated (`StatedBytes`; 0 states none). */
	static bool SizeMatches(int64 StatedBytes, int64 ActualBytes);

	// The words of each refusal. One home, so the log, the Python helper and the tests say the same thing.
	static FString NoTilesListedReason();
	static FString NoTilesBesideReason();
	static FString NotFetchedYetMessage();
	static FString NotATilesetReason();
	static FString NotThreeReason();
};
