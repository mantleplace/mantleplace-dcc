// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow

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
	 * are skipped rather than failing the layer. Fails closed (false + OutError) only when the
	 * header row is missing/unrecognized.
	 */
	static bool ParseCsv(
	    const FString& CsvText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    TArray<FMantlePlaceTreePointRow>& OutRows,
	    FString& OutError);
};
