// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow

/**
 * How a tree-points parse ended. Two ways of not producing a table, deliberately kept apart,
 * because they are not the same news for the user or for the import as a whole.
 */
enum class EMantlePlaceTreePointsOutcome : uint8
{
	/** Rows are usable. When the manifest published a `point_count`, it agreed with them. */
	Parsed,

	/**
	 * The CSV's header is not the column contract this reader speaks. The layer is SKIPPED and the
	 * rest of the import stands: the ETL changed a payload's shape, which is a fact about the
	 * bundle rather than a failure of the terrain, the imagery or the buildings beside it.
	 */
	HeaderUnrecognised,

	/**
	 * The payload and the manifest disagree about how many points there are. This one IS a failure
	 * of the import: the rows that did parse are a silent subset of the layer, and a foliage
	 * scatter built from a subset reads as a sparse forest rather than as an error.
	 */
	CountMismatch,
};

/**
 * Pure (engine-/IO-free) logic for the tree-points layer: Landcover/TreePoints.csv text ->
 * DataTable rows in the Local Projected Frame. The CSV ships absolute AOI-UTM x/y (the DEM's
 * CRS) so no geographic projection is needed — just the same origin-relative frame math the
 * rest of the importer uses. Deterministic and headless-testable under -nullrhi; the importer
 * shim owns the impure parts (zip read, UDataTable asset creation).
 */
struct FMantlePlaceTreePointsLogic
{
	/**
	 * Parse the ETL's tree-points CSV (header "x,y,ground_z,height_m,crown_radius_m"; ground_z
	 * may be empty when the DEM had no data — Position.Z then stays 0). Rows that fail to parse
	 * are skipped rather than failing the layer, and OutError is filled on every non-Parsed
	 * outcome.
	 *
	 * `DeclaredPointCount` is `unreal.foliage_points.point_count`, or 0 when the manifest
	 * published none. The two halves of the tree-points integrity story are deliberately
	 * different checks: the sha256 proves the BYTES are the bytes the platform hashed, and the
	 * count proves the ROWS survived this reader — a CSV whose columns drifted parses to a
	 * shorter table with the digest still matching, and skipping malformed rows is exactly what
	 * makes that silent. Zero means unknown, never zero rows: a published count of 0 with no
	 * points is indistinguishable from an absent one, and both import cleanly.
	 */
	static EMantlePlaceTreePointsOutcome ParseCsv(
	    const FString& CsvText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 DeclaredPointCount,
	    TArray<FMantlePlaceTreePointRow>& OutRows,
	    FString& OutError);
};
