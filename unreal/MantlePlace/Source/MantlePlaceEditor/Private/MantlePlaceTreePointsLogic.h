// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow

/**
 * How a tree-points parse ended. Three ways of not producing a table, deliberately kept apart,
 * because they are not the same news for the user or for the import as a whole.
 */
enum class EMantlePlaceTreePointsOutcome : uint8
{
	/** Rows are usable. When the manifest published a `point_count`, it agreed with them. */
	Parsed,

	/**
	 * The CSV's header lacks a column this reader needs (or there is no header at all). A column it
	 * does not know is NOT this outcome: columns are found by name and extras are ignored. The
	 * layer is SKIPPED and the rest of the import stands: the ETL changed a payload's shape, which
	 * is a fact about the bundle rather than a failure of the terrain, the imagery or the buildings
	 * beside it.
	 */
	HeaderUnrecognised,

	/**
	 * The payload and the manifest disagree about how many points there are. This one IS a failure
	 * of the import: the rows that did parse are a silent subset of the layer, and a foliage
	 * scatter built from a subset reads as a sparse forest rather than as an error.
	 */
	CountMismatch,

	/**
	 * The file cannot be placed in this host's frame (HPS-53). Unreal is a fixed-frame host: its
	 * origin is metric UTM on every order, while the file is in whatever frame the delivery tier
	 * gave it, and `unreal.foliage_points` states no CRS and no unit to tell the two apart. What
	 * the block DOES publish is the landscape's extent, and a point outside it is not in this
	 * frame. The layer is SKIPPED with the reason, nothing is converted — no unit is scaled and
	 * nothing is reprojected (HPS-33) — and the rest of the import stands.
	 */
	Unplaceable,
};

/**
 * Pure (engine-/IO-free) logic for the tree-points layer: Landcover/TreePoints.csv text ->
 * DataTable rows in the Local Projected Frame. On a metric order the CSV ships absolute AOI-UTM
 * x/y, which is this host's frame, so no geographic projection is needed — just the same
 * origin-relative frame math the rest of the importer uses. On another delivery tier the file is
 * on another grid, and nothing beside the pointer says so; what this unit can see is that such
 * coordinates fall off the landscape, and it refuses the file on that evidence rather than
 * subtracting an origin it does not share. Deterministic and headless-testable under -nullrhi;
 * the importer shim owns the impure parts (zip read, UDataTable asset creation).
 */
struct FMantlePlaceTreePointsLogic
{
	/**
	 * Parse the ETL's tree-points CSV. Columns are resolved from the header BY NAME (trimmed,
	 * case-insensitive), in any order: x, y, ground_z, height_m and crown_radius_m are required
	 * and any other column is ignored, so the platform can add one without dropping the layer.
	 * ground_z may be empty when the DEM had no data — Position.Z then stays 0. Rows that fail to
	 * parse (too few fields, non-numeric x/y) are skipped rather than failing the layer, and
	 * OutError is filled on every non-Parsed outcome.
	 *
	 * `DeclaredPointCount` is `unreal.foliage_points.point_count`, or 0 when the manifest
	 * published none. The two halves of the tree-points integrity story are deliberately
	 * different checks: the sha256 proves the BYTES are the bytes the platform hashed, and the
	 * count proves the ROWS survived this reader — a CSV whose columns drifted parses to a
	 * shorter table with the digest still matching, and skipping malformed rows is exactly what
	 * makes that silent. Zero means unknown, never zero rows: a published count of 0 with no
	 * points is indistinguishable from an absent one, and both import cleanly.
	 *
	 * `LandscapeSpanUeCm` is `FMantlePlaceVaultManifest::GetAoiSizeUeCm()`: the landscape's
	 * published span in UE axis order (X = North, Y = East), centred on the origin; zero when the
	 * block publishes no heightmap. It is the one backstop HPS-53 permits where the format states
	 * no frame beside the pointer, and it is a comparison of published values, never a derivation.
	 * **It runs one way.** Any row outside the span makes the FILE Unplaceable — the frame belongs
	 * to the file, not to the point, so rows that happen to land inside are not kept — and so does
	 * a span of zero, because an unstated frame is never assumed to match. Rows inside the span
	 * prove nothing about the frame; they are only the absence of evidence against it, and Parsed
	 * does not claim otherwise. The check runs before the count: whether every row survived is a
	 * question about a file this host places.
	 */
	static EMantlePlaceTreePointsOutcome ParseCsv(
	    const FString& CsvText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    const FVector2D& LandscapeSpanUeCm,
	    int32 DeclaredPointCount,
	    TArray<FMantlePlaceTreePointRow>& OutRows,
	    FString& OutError);
};
