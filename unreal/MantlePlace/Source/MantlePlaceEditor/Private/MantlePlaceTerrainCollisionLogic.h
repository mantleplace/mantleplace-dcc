// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/** The collision an imported mesh is given. Set explicitly on every import, never left to Interchange. */
enum class EMantlePlaceMeshCollision : uint8
{
	/**
	 * Blocks as its own triangles: complex collision used as simple, with no simple shapes. Interchange's
	 * glTF default is one 18-sided convex hull around the whole mesh, which on a terrain or a site's
	 * massing is a lid over everything, so a trace or a body meets the hull instead of the ground.
	 */
	Surface,
	/** No collision: something else in the same import is the ground. */
	None,
};

/**
 * Which imported terrain collides. One ground, never two near-coincident ones: in Both mode the Landscape
 * collides and the terrain mesh laid over it does not, so a body rests on the surface the Landscape
 * draws rather than on whichever of the two is a few centimetres higher at that point.
 */
namespace MantlePlaceTerrainCollisionLogic
{
	/** The terrain mesh's collision, given whether this import built a Landscape. */
	EMantlePlaceMeshCollision ForTerrainMesh(bool bLandscapeBuilt);

	/** The building massing's collision: always its own surface. */
	EMantlePlaceMeshCollision ForBuildings();
}
