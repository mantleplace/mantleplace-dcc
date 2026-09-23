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
	 * origin is metric UTM on every order, while a tree-point file can be in whatever frame the
	 * delivery tier gave it. From MPB 1.3.0 `unreal.foliage_points` states the file's CRS and units,
	 * and a stated frame that is not this host's — or one the pointer fails to state in full — is
	 * this outcome. Before 1.3.0 it states none, and a point outside the landscape extent the block
	 * publishes is the evidence instead; that extent test stays as the backstop behind a stated
	 * frame too, because a producer can state a frame wrongly. The layer is SKIPPED with the
	 * reason, nothing is converted — no unit is scaled and nothing is reprojected (HPS-33) — and
	 * the rest of the import stands.
	 */
	Unplaceable,
};

/**
 * The frame `unreal.foliage_points` states for its file, beside the frame this host places in.
 * Every string is the manifest's, verbatim; empty means the manifest did not state it. Built by
 * `FMantlePlaceVaultManifest::GetFoliagePointsFrame()`, so the parser decides what was published
 * and this unit decides what that means.
 */
struct FMantlePlaceTreePointsFrame
{
	FString Crs;             // unreal.foliage_points.crs — the CRS `x` and `y` are in
	FString Units;           // unreal.foliage_points.units — the unit of `ground_z`
	FString HorizontalUnits; // unreal.foliage_points.horizontal_units — the unit of `x` and `y`

	/** unreal.georeference.crs_projected: this host's frame, the one its origin is stated in. */
	FString HostCrs;

	/**
	 * The manifest's version is one whose format requires the pointer to state its frame (MPB 1.3.0
	 * and later). A required frame that is missing is refused rather than read as "pre-1.3.0": the
	 * version is what says which kind of bundle this is, never the absence of a key (HPS-35).
	 */
	bool bRequired = false;

	/** Any of the three frame keys was published, or the version says they must be. */
	bool IsStated() const
	{
		return bRequired || !Crs.IsEmpty() || !Units.IsEmpty() || !HorizontalUnits.IsEmpty();
	}
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
	 * `Frame` is what the pointer states (HPS-53). Where it states a frame, the file is placed only
	 * if that frame is this host's: the CRS equal to `georeference.crs_projected` and both units
	 * `m`, each compared verbatim — a stated frame that is anything else, or one stated only in
	 * part, is Unplaceable before a row is read. Where the pointer states none (a manifest older
	 * than 1.3.0), the extent below is the whole of the check.
	 *
	 * `LandscapeSpanUeCm` is `FMantlePlaceVaultManifest::GetAoiSizeUeCm()`: the landscape's
	 * published span in UE axis order (X = North, Y = East), centred on the origin; zero when the
	 * block publishes no heightmap. It is a comparison of published values, never a derivation,
	 * and **it runs one way.** Any row outside the span makes the FILE Unplaceable — the frame
	 * belongs to the file, not to the point, so rows that happen to land inside are not kept.
	 * Rows inside the span prove nothing about the frame; they are only the absence of evidence
	 * against it. A span of zero is therefore no evidence either way: behind a stated frame that
	 * matched, the file is placed (a mesh-only bundle brings its trees), and behind an unstated
	 * one it is refused, because an unstated frame is never assumed to match. The check runs
	 * before the count: whether every row survived is a question about a file this host places.
	 */
	static EMantlePlaceTreePointsOutcome ParseCsv(
	    const FString& CsvText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    const FMantlePlaceTreePointsFrame& Frame,
	    const FVector2D& LandscapeSpanUeCm,
	    int32 DeclaredPointCount,
	    TArray<FMantlePlaceTreePointRow>& OutRows,
	    FString& OutError);
};
