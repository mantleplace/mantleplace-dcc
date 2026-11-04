// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/** A WGS84 extent in degrees: west, south, east, north. The order of the curator link's `aoi` token. */
struct MANTLEPLACERUNTIME_API FMantlePlaceExtent
{
	double West = 0.0;
	double South = 0.0;
	double East = 0.0;
	double North = 0.0;

	/** Finite, inside the world, and with area: West < East and South < North. */
	bool IsValid() const;
};

/** One point of the level, as a longitude/latitude pair in WGS84 degrees. */
struct FMantlePlaceGeoPoint
{
	double Longitude = 0.0;
	double Latitude = 0.0;
};

/**
 * What an Extent box says about itself: where on Earth its centre is (the user types it, because a
 * level with no georeference has no other way to know) and how big it is (the user scales it).
 * Sizes are metres along the box's X (east-west) and Y (north-south) axes.
 */
struct FMantlePlaceExtentBoxState
{
	double CenterLongitude = 0.0;
	double CenterLatitude = 0.0;
	double WidthM = 0.0;
	double DepthM = 0.0;
};

/** Where an extent came from, in the order Open in curator tries them. */
enum class EMantlePlaceExtentSource : uint8
{
	/** The level holds no source: the plain curator opens. */
	None,
	/** A previous Mantle Place import's provenance record. */
	PriorImport,
	/** The level's native georeferencing (Epic's GeoReferencing system actor). */
	NativeGeoreference,
	/** A Cesium georeference actor, read by reflection so Cesium stays optional. */
	CesiumGeoreference,
	/** A Mantle Place Extent box the user placed and scaled. */
	ExtentBox,
};

/**
 * The level as the sources need to see it, already read out of the engine. Each array is empty when
 * the level has nothing of that kind, which is what "this source does not apply" means.
 */
struct FMantlePlaceLevelState
{
	/** The extents recorded by the Mantle Place imports whose actors are in the level. */
	TArray<FMantlePlaceExtent> ImportExtents;

	/** The corners of the level's bounds, converted to degrees by the native georeference. */
	TArray<FMantlePlaceGeoPoint> NativeGeoreferencedPoints;

	/** The same corners, converted to degrees by a Cesium georeference. */
	TArray<FMantlePlaceGeoPoint> CesiumGeoreferencedPoints;

	/** Every Extent box in the level. */
	TArray<FMantlePlaceExtentBoxState> ExtentBoxes;
};

/** The extent Open in curator will use, and the source that supplied it. */
struct FMantlePlaceResolvedExtent
{
	EMantlePlaceExtentSource Source = EMantlePlaceExtentSource::None;
	FMantlePlaceExtent Extent;

	bool IsSet() const { return Source != EMantlePlaceExtentSource::None; }
};

/**
 * Pure (engine-free) logic for Open in curator: the four extent sources, the precedence among them,
 * and the link they build. Reading the engine (finding actors, converting bounds through a
 * georeference) is the editor module's job; every decision is here, where an automation test can
 * assert it without a level.
 *
 * The plugin applies pre-derived values and never derives them (root CLAUDE.md). Open in curator is
 * not a placement: nothing here is applied to the level. It reads the extent the level already
 * knows and hands it to the web, where the curator draws it. Interop only: nothing streams from
 * Cesium or from Mantle Place.
 */
struct MANTLEPLACERUNTIME_API FMantlePlaceCuratorExtentLogic
{
	/** The `from` value the curator records for a handoff from this host. */
	static const TCHAR* From();

	/** Source 1. The union of the valid extents; false when there is none. */
	static bool FromPriorImports(const TArray<FMantlePlaceExtent>& ImportExtents, FMantlePlaceExtent& OutExtent);

	/**
	 * Sources 2 and 3: the box around points a georeference placed. False when there are fewer than
	 * two points, when any is not a finite longitude/latitude (a broken georeference must not yield
	 * a partial extent), or when the box has no area or wraps the antimeridian.
	 */
	static bool FromNativeGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent);
	static bool FromCesiumGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent);

	/**
	 * Source 4. The union of the extents of the boxes that can be read. A box reads when its size is
	 * positive and finite and its centre is a located point: exactly (0, 0) is how a box nobody has
	 * located yet reads, and it is refused rather than sent to the Gulf of Guinea. Rotation is
	 * ignored on purpose: W,S,E,N is axis-aligned, X is east and Y is north.
	 */
	static bool FromExtentBoxes(const TArray<FMantlePlaceExtentBoxState>& Boxes, FMantlePlaceExtent& OutExtent);

	/** One box, as FromExtentBoxes reads it. */
	static bool FromExtentBox(const FMantlePlaceExtentBoxState& Box, FMantlePlaceExtent& OutExtent);

	/** Try the sources in order, prior import, native georeference, Cesium georeference, Extent box. */
	static FMantlePlaceResolvedExtent Resolve(const FMantlePlaceLevelState& Level);

	/** The source in words, for the log and for the message that says what the curator opened on. */
	static FString DescribeSource(EMantlePlaceExtentSource Source);

	/**
	 * The link: `<base>/curator#aoi=W,S,E,N&from=unreal` for an extent, the plain `<base>/curator`
	 * for none. Bounds print at the curator's own precision (4 decimals, trailing zeros trimmed),
	 * rounded outward so the drawn extent always contains the level's. A base that is not http(s)
	 * yields an empty string, meaning "open nothing".
	 */
	static FString BuildCuratorUrl(const FString& BaseUrl, const FMantlePlaceResolvedExtent& Resolved);

	/** One bound at the curator's precision. `bRoundUp` rounds toward +infinity, else toward -infinity. */
	static FString FormatBound(double Value, bool bRoundUp);

	/** The `W,S,E,N` token for an extent. */
	static FString FormatToken(const FMantlePlaceExtent& Extent);
};
