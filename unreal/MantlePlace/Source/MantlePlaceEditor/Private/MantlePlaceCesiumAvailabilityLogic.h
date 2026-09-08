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
	 * merge, and a rectangle that claimed more than one tile would reintroduce the very defect this
	 * corrects.
	 */
	static TArray<TSharedPtr<FJsonValue>> BuildAvailability(const TArray<FMantlePlaceCesiumTile>& Tiles);

	/**
	 * Whether `Root`'s existing `available` already lists exactly `Tiles` and nothing else.
	 *
	 * `OutDeclaredTileCount` is filled either way with how many tiles the file declares (the sum of
	 * its rectangle areas), so a caller can log the mismatch with numbers in it. It is the count that
	 * makes an upstream report actionable: "declares 8, ships 1" is a bug report, "availability is
	 * wrong" is not.
	 */
	static bool IsAvailabilityConsistent(
		const TSharedPtr<FJsonObject>& Root,
		const TArray<FMantlePlaceCesiumTile>& Tiles,
		int64& OutDeclaredTileCount);
};
