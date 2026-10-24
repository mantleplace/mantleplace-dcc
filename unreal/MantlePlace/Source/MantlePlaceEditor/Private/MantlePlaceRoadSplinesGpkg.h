// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

#include "MantlePlaceRoadSplinesLogic.h"

/**
 * Reads the shared `road_splines` layer from its GeoPackage (`Vector/RoadSplines.gpkg`), the file
 * the layer's `gpkg` format row in `vector.layers` points at. The same layer as the GeoJSON, in the
 * same geographic frame, so the roads that come out are the roads ParseGeoJson produces: width,
 * class and name are the columns of the same names, and the projection into the Local Projected
 * Frame is FMantlePlaceRoadSplinesLogic's one projection.
 *
 * SQLite is read through the engine's own SQLiteCore module (FSQLiteDatabase), never a vendored
 * copy. Everything but the file write is a pure function of the bytes, headless-testable on a
 * synthetic GeoPackage.
 */
struct FMantlePlaceRoadSplinesGpkg
{
	/** The only geographic frame this reader places: the layer's coordinates are WGS84 lon/lat. */
	static constexpr int32 Wgs84SrsId = 4326;

	/**
	 * Read the road lines of a GeoPackage held in memory. SQLite opens a file, so the bytes are
	 * written to a scratch file under the project's Intermediate folder for the duration of the
	 * read and deleted before this returns.
	 */
	static bool ReadBytes(
	    const TArray<uint8>& GpkgBytes,
	    const TArray<FString>& Tables,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    int32& OutRowsUnreadable,
	    FString& OutError);

	/**
	 * Read the road lines of a GeoPackage on disk. `Tables` is the `tables` member of the layer's
	 * format row (a layer split by geometry family names one table per family). Empty means the row
	 * lists none, so the file holds exactly one feature table and that one is read whatever it is
	 * named (the producer names it in CamelCase, `RoadSplines`, not after the layer); a file of
	 * several is refused, never read as its only one. A feature's own geometry type decides whether it is a road line:
	 * LineString and MultiLineString are (each part its own spline, as in ParseGeoJson), and any
	 * other type is skipped.
	 *
	 * Fails closed (false + OutError) on an Epsg that is not a UTM zone (checked first, as
	 * ParseGeoJson does), a file that is not a GeoPackage, a named table the file does not hold,
	 * an untabled row whose file holds more than one feature table,
	 * and a table whose geometry column is not in EPSG:4326 -- the roads are never reprojected from
	 * another frame (HPS-33). A single feature whose geometry blob cannot be read is counted in
	 * OutRowsUnreadable and passed over, so one bad row does not drop the layer.
	 */
	static bool ReadFile(
	    const FString& GpkgFilePath,
	    const TArray<FString>& Tables,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    int32& OutRowsUnreadable,
	    FString& OutError);

	/**
	 * Decode one GeoPackage geometry blob (the standard header, then ISO or extended WKB, either
	 * byte order) into its line parts as (lon, lat, z) points; z is 0 where the geometry has none.
	 * A LineString yields one part, a MultiLineString one per member, an empty geometry or any other
	 * type none (OutIsLine is then false: not a road centreline, to be skipped, which is not an
	 * error). Returns false, with OutError, for a blob that is truncated or malformed.
	 */
	static bool DecodeGeometry(
	    const TArray<uint8>& Blob,
	    TArray<TArray<FVector>>& OutParts,
	    bool& OutIsLine,
	    FString& OutError);
};
