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
 * The vertical datum the shared `road_splines` layer states for its heights (MPB 1.8.0), beside
 * whether the manifest's version owes that statement. Built by
 * `FMantlePlaceVaultManifest::GetRoadSplinesDatum()`, so the parser reports what was published and
 * FMantlePlaceRoadSplinesLogic::CanPlaceHeights decides what it means.
 */
struct FMantlePlaceRoadSplinesDatum
{
	/** vector.layers[road_splines].vertical_datum, verbatim; empty when the layer states none. */
	FString Stated;

	/**
	 * The manifest's version is one whose format states the datum of a layer's heights (MPB 1.8.0
	 * and later). There, a road layer that states none is refused rather than read as a bundle from
	 * before the format could say: the version decides which kind of bundle this is, never the
	 * absence of the key.
	 */
	bool bRequired = false;
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
	/** True when Epsg is a WGS84 UTM zone: 326xx (north, 1-60) or 327xx (south, 1-60). */
	static bool IsUtmEpsg(int32 Epsg);

	/**
	 * WGS84 lon/lat (degrees) -> UTM easting/northing (meters) for the zone encoded in Epsg
	 * (326xx = north, 327xx = south). Returns false for a non-UTM EPSG or out-of-range input.
	 */
	static bool LonLatToUtm(double LonDeg, double LatDeg, int32 Epsg, double& OutEastingM, double& OutNorthingM);

	/**
	 * Whether the road layer's heights are in this host's vertical datum, so its splines sit on the
	 * landscape rather than above or below it. This host is a fixed-frame host (HPS-54), and fixed
	 * vertically too: its landscape, mesh and foliage are EGM2008 orthometric heights on
	 * every order, while the shared layer's heights are the delivered elevation's, which on some
	 * deliveries is another datum.
	 *
	 * True when the layer states `EGM2008-orthometric`, compared verbatim, or when it states nothing
	 * and the version owes nothing — a bundle from before the format stated a datum, when every
	 * height it carried was EGM2008. False, with OutReason naming what was stated, for any other
	 * datum, an unknown one included (the format makes an unknown datum fail closed), and for an
	 * owed datum the layer leaves out. The refusal is a named skip of the layer, as HPS-53 skips a
	 * file in another frame, and never a conversion: no offset is applied, because shifting a height
	 * is deriving one (HPS-33).
	 */
	static bool CanPlaceHeights(const FMantlePlaceRoadSplinesDatum& Datum, FString& OutReason);

	/**
	 * Parse a RoadSplines GeoJSON FeatureCollection into Local-Projected-Frame splines.
	 * OriginEastingM/OriginNorthingM/Epsg come from the manifest's unreal.georeference block.
	 * LineString and MultiLineString geometries are accepted (each MultiLineString part becomes
	 * its own spline, mirroring the ETL's per-part rows); other geometry types are skipped.
	 * Fails closed (false + OutError) on invalid JSON, a missing features array, or an Epsg that
	 * is not a UTM zone. The last is checked first and refuses the whole layer, empty or not: the
	 * origin is what is wrong, and left to the per-point projection it would drop every point of
	 * every road and return success with zero splines.
	 *
	 * `OutLinesSeen` counts the line parts the layer carried (a MultiLineString part counts once,
	 * a Point never), whether or not they could be placed. It exists because dropping is invisible
	 * on its own: a layer of roads that all failed and a layer with no roads both return zero
	 * splines, and the count is what lets the caller say which happened. See DescribeOutcome.
	 */
	static bool ParseGeoJson(
	    const FString& JsonText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    FString& OutError);

	/**
	 * The importer's one-line status for a road layer that parsed: how many line parts were
	 * present against how many became splines. An empty layer reads as a plain success; a layer
	 * whose roads were all unplaceable, or some of them, says so and names both numbers, so the
	 * two cannot be mistaken for each other.
	 */
	static FString DescribeOutcome(int32 LinesSeen, int32 SplinesCreated);
};
