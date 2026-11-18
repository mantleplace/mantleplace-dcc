// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * One point of the live terrain-collision test's roof check (`MantlePlace.Import.LiveTerrainCollision`), in
 * world centimetres. A roof point is where a complex (per-triangle) trace down onto the building massing
 * alone meets an upward-facing surface.
 */
struct FMantlePlaceRoofSample
{
	/** Where the complex trace onto the buildings alone met the roof. */
	double RoofZ = 0.0;
	/** Whether a default trace onto the terrain alone (the buildings and every other actor ignored) hit. */
	bool bTerrain = false;
	/** Where it hit: the ground the level collides with under the point. */
	double TerrainZ = 0.0;
	/** The rendered ground under the point (the Landscape's own heights) and one heightmap sample's relief. */
	double GroundZ = 0.0;
	double GroundReliefCm = 0.0;
	/** The default trace down through the whole level, as a game asks: whether it hit, what, and where. */
	bool bHit = false;
	bool bHitBuildings = false;
	bool bHitTerrain = false;
	double HitZ = 0.0;
};

enum class EMantlePlaceRoofTraceVerdict : uint8
{
	/** The roof is open to the sky here, and the trace met it (the buildings, at the roof). */
	AtRoof,
	/** The drawn ground lies over the roof here, and the trace met that ground. */
	AtCoveringGround,
	/** Anything else: a fall-through, a hull above the roof, or a ground that is not where it is drawn. */
	Missed,
};

/**
 * The judgement of one roof point. This is test support: it lives with the tests and is read by nothing the
 * plugin ships.
 *
 * A building on a slope is levelled to its lowest ground and extruded from there, so on ground that rises
 * across its footprint by more than its height the ground at the uphill end is drawn over its roof. There
 * the first surface a trace down meets is the ground, and that is correct: the roof is not where the trace
 * should stop. Such a point is judged against the ground, and only the drawn ground may cover a roof: a
 * terrain collision above the rendered ground (beyond one heightmap sample's relief) excuses nothing.
 */
struct FMantlePlaceRoofTraceLogic
{
	/** The height a hit may sit off the surface it is judged against, in centimetres. */
	static constexpr double SlackCm = 2.0;

	/** The terrain under the point is over the roof by more than the slack. */
	static bool IsCovered(const FMantlePlaceRoofSample& Sample);

	static EMantlePlaceRoofTraceVerdict Judge(const FMantlePlaceRoofSample& Sample);
};
