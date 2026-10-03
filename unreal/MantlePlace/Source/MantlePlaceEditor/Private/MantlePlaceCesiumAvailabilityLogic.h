// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

class FJsonObject;
class FJsonValue;

/** One quantized-mesh tile, as its `{z}/{x}/{y}.terrain` path names it. */
struct FMantlePlaceCesiumTile
{
	int32 Zoom = 0;
	int32 X = 0;
	int32 Y = 0;

	bool operator==(const FMantlePlaceCesiumTile& Other) const
	{
		return Zoom == Other.Zoom && X == Other.X && Y == Other.Y;
	}
};

/**
 * How a `layer.json`'s declared availability differs from the tiles on disk. Every count is filled
 * whether or not the two agree, because the counts are what turn a rewrite into an upstream report.
 */
struct FMantlePlaceCesiumAvailabilityDiff
{
	/** Distinct tiles the file declares. For a level too large to expand, its rectangle areas summed. */
	int64 DeclaredTileCount = 0;

	/** Declared tiles that are not on disk — what makes Cesium 404. */
	int64 AbsentTileCount = 0;

	/** Tiles on disk that no rectangle declares — what Cesium will never request. */
	int64 UndeclaredTileCount = 0;
};

/**
 * Pure logic for the quantized-mesh `layer.json` availability workaround.
 *
 * The defect being worked around is upstream, in packaging: a bundle's `layer.json` declares the
 * whole-world pyramid at low zooms (level 1 claims x[0..3] y[0..1] = 8 tiles) while only the single
 * AOI-ancestor tile per level is actually written. Cesium for Unreal trusts `available`, requests
 * the declared-but-absent siblings, gets 404s, and aborts the whole tileset with "Errors loading
 * quantized mesh terrain" — nothing renders. The plugin's local server rewrites `available` to list
 * exactly the tiles present, which is what the web app does too.
 *
 * The workaround's own comment has always said upstream is the fix's home, and the honest question
 * is whether it is still needed at all — a question nobody can answer from the source, only from a
 * bundle. So the decision is asked out loud rather than assumed: `IsAvailabilityConsistent` says
 * whether this bundle's `layer.json` already agrees with its tiles, and the caller logs the answer
 * either way. A bundle that is already consistent says so in the log, which is the evidence that
 * this code can be deleted; a bundle that is not says by how much, which is the evidence for the
 * upstream report.
 *
 * Pure and headless: no filesystem, no engine. `MantlePlace.Import.CesiumAvailabilityLogic` asserts
 * it, which is what makes the question answerable at all — the rewrite used to live inside the
 * streaming path, where nothing about it could be checked without a bundle and a running editor.
 */
struct FMantlePlaceCesiumAvailabilityLogic
{
	/**
	 * Read `{z}/{x}/{y}` out of a tile path, from its trailing path components rather than by
	 * relativising against the terrain directory — separator style is then not something to get
	 * right. Returns false, leaving OutTile untouched, for a path whose three components are not
	 * all unsigned integers.
	 *
	 * Digits only, deliberately: `FString::IsNumeric` accepts a sign, a decimal point and an
	 * exponent, and `"1.5"` reaching `Atoi` would silently become tile 1. A tile coordinate is a
	 * non-negative integer or it is not a tile.
	 */
	static bool ParseTilePath(const FString& TilePath, FMantlePlaceCesiumTile& OutTile);

	/**
	 * The `available` array a `layer.json` should carry for exactly these tiles: one inclusive 1x1
	 * rectangle per tile, grouped by zoom, with an entry for every level from 0 to the deepest tile's
	 * even when a level is empty (the array is indexed by level, so a gap would shift every level
	 * below it).
	 *
	 * One rectangle per tile rather than merged runs, because the present tiles form a connected
	 * descent chain to the AOI — one tile per level until the AOI's own zoom — so there is nothing to
	 * merge, and a 1x1 rectangle cannot claim a tile that is not there.
	 */
	static TArray<TSharedPtr<FJsonValue>> BuildAvailability(const TArray<FMantlePlaceCesiumTile>& Tiles);

	/**
	 * Whether `Root`'s existing `available` already lists exactly `Tiles` and nothing else.
	 *
	 * Judged by tile set, level by level: each declared rectangle is expanded, inclusive on both
	 * ends, into the tiles it covers, and a level is consistent when the tiles it declares are the
	 * tiles present at that zoom. A rectangle wider or taller than one tile is therefore fine when
	 * every tile it covers is on disk — a bundle that declares zoom 0 as one rectangle over its two
	 * root tiles is right, not defective. A rectangle that covers an absent tile is the real upstream
	 * defect. A malformed rectangle is a file this cannot read, so it is never called consistent.
	 *
	 * `OutDiff` is filled either way, so a caller can log a mismatch with numbers in it — see
	 * `DescribeMismatch`.
	 */
	static bool IsAvailabilityConsistent(
		const TSharedPtr<FJsonObject>& Root,
		const TArray<FMantlePlaceCesiumTile>& Tiles,
		FMantlePlaceCesiumAvailabilityDiff& OutDiff);

	/**
	 * The whole correction: when `Root`'s `available` is not consistent with `Tiles`, replace it with
	 * `BuildAvailability(Tiles)` and return true; when it is, leave `Root` untouched and return false.
	 * The caller writes the file back only on true, so a consistent `layer.json` is never rewritten.
	 */
	static bool CorrectAvailability(
		const TSharedPtr<FJsonObject>& Root,
		const TArray<FMantlePlaceCesiumTile>& Tiles,
		FMantlePlaceCesiumAvailabilityDiff& OutDiff);

	/**
	 * The warning a rewrite logs. It states what differed, not only the two counts: equal counts can
	 * hide different tiles, and "declares 47 but ships 47" reads as a contradiction.
	 */
	static FString DescribeMismatch(
		const FString& JobId, const FMantlePlaceCesiumAvailabilityDiff& Diff, int32 PresentTileCount);
};
