// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

#include "MantlePlaceCuratorExtentLogic.h"

class AMantlePlaceExtentBox;
class UWorld;

/**
 * Open in curator's reads: what the editor's level says, handed to FMantlePlaceCuratorExtentLogic
 * (which decides). This is the thin, engine-touching half; nothing here chooses between sources or
 * builds a link.
 *
 * Nothing is applied to the level and nothing streams. Cesium and Epic's GeoReferencing are read by
 * REFLECTION -- a class looked up by name, a function called by name -- so this module takes no
 * dependency on either, and a project without them loads and builds the plugin unchanged. Where a
 * class is not loaded its source is simply absent.
 */
namespace MantlePlaceLevelExtent
{
	/** Epic's GeoReferencing system actor (engine plugin "GeoReferencing", not a dependency of this plugin). */
	const TCHAR* NativeGeoreferenceClassPath();

	/** Cesium for Unreal's georeference actor (a third-party plugin, never a dependency of this plugin). */
	const TCHAR* CesiumGeoreferenceClassPath();

	/**
	 * The level's bounds in world space (cm): a partitioned world's editor bounds, else the bounds of
	 * the loaded landscape and static mesh actors. Invalid when the level holds none. Sky, fog and
	 * lights are left out on purpose: their bounds are not the site's.
	 */
	FBox LevelBounds(UWorld* World);

	/**
	 * The four corners of the bounds, converted to longitude/latitude by the first actor of the named
	 * class in the level. `bNative` picks which function is called (`EngineToGeographic` on Epic's
	 * system, `TransformUnrealPositionToLongitudeLatitudeHeight` on Cesium's). Empty when the class is
	 * not loaded, the level has no such actor, the bounds are empty, or the call cannot be made.
	 */
	TArray<FMantlePlaceGeoPoint> ConvertCorners(UWorld* World, const FBox& Bounds, bool bNative);

	/** The extents recorded by the Mantle Place imports whose actors are in the level (provenance). */
	TArray<FMantlePlaceExtent> ImportExtents(UWorld* World);

	/** Every Extent box in the level, as the logic reads it. */
	TArray<FMantlePlaceExtentBoxState> ExtentBoxes(UWorld* World);

	/** Read the level into the state the logic decides on. */
	FMantlePlaceLevelState Gather(UWorld* World);

	/** Gather, then decide. */
	FMantlePlaceResolvedExtent Resolve(UWorld* World);

	/**
	 * Place an Extent box (a transaction, so undo removes it) at the middle of the level's bounds
	 * or the origin, label it, and select it so Details shows where to locate it. A level that
	 * already has one gets that one selected instead of a second. Null without a world.
	 */
	AMantlePlaceExtentBox* PlaceExtentBox(UWorld* World);
}
