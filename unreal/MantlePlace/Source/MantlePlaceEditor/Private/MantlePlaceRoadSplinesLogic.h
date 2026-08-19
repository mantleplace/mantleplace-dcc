// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * One Z-draped road centerline from the bundle's Vector/RoadSplines.geojson, converted to the
 * Local Projected Frame (AOI centroid at world origin, East -> +X, North -> +Y, 1 uu = 1 cm).
 * WidthMEstimated / RoadClass / Name ride along for the spawned actor's metadata tags.
 */
struct FMantlePlaceRoadSpline
{
	TArray<FVector> PointsUeCm;
	double WidthMEstimated = 0.0;
	FString RoadClass;
	FString Name;
};

/**
 * Pure (engine-/IO-free) logic for the road-splines layer: GeoJSON text -> spline point sets in
 * the Local Projected Frame. The GeoJSON ships WGS84 lon/lat (RFC 7946) with orthometric Z in
 * meters, so this owns the one place the plugin projects geographic coordinates: a WGS84 ->
 * UTM transverse-Mercator forward (USGS/Snyder series, sub-decimeter within a zone — ample for
 * road centerlines at engine scale). Everything is deterministic and headless-testable under
 * -nullrhi (mirrors FMantlePlaceVaultLogic / FMantlePlaceAuthLogic); the importer shim owns the
 * impure parts (zip read, actor spawning).
 */
struct FMantlePlaceRoadSplinesLogic
{
	/**
	 * WGS84 lon/lat (degrees) -> UTM easting/northing (meters) for the zone encoded in Epsg
	 * (326xx = north, 327xx = south). Returns false for a non-UTM EPSG or out-of-range input.
	 */
	static bool LonLatToUtm(double LonDeg, double LatDeg, int32 Epsg, double& OutEastingM, double& OutNorthingM);

	/**
	 * Parse a RoadSplines GeoJSON FeatureCollection into Local-Projected-Frame splines.
	 * OriginEastingM/OriginNorthingM/Epsg come from the manifest's unreal.georeference block.
	 * LineString and MultiLineString geometries are accepted (each MultiLineString part becomes
	 * its own spline, mirroring the ETL's per-part rows); other geometry types are skipped.
	 * Fails closed (false + OutError) only on invalid JSON or a missing features array.
	 */
	static bool ParseGeoJson(
	    const FString& JsonText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    FString& OutError);
};
