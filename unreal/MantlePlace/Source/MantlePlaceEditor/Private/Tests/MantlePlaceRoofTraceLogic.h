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
	/**
	 * The rendered ground under the point: the Landscape's own heights, read from its height data, which no
	 * collision setting changes. Whether the ground covers the roof is decided from this, never from TerrainZ.
	 */
	double GroundZ = 0.0;
	/** One heightmap sample's relief around the point: reported, and no part of the judgement. */
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
	/** The drawn ground lies over the roof here, the terrain collides where it is drawn, and the trace met it. */
	AtCoveringGround,
	/** Anything else: a fall-through, a hull above the roof, or a terrain that does not collide where it is drawn. */
	Missed,
};

/**
 * The judgement of one roof point. This is test support: it lives with the tests and is read by nothing the
 * plugin ships.
 *
 * A building on a slope is levelled to its lowest ground and extruded from there, so on ground that rises
 * across its footprint by more than its height the ground at the uphill end is drawn over its roof. There
 * the first surface a trace down meets is the ground, and that is correct: the roof is not where the trace
 * should stop. Such a point is judged against the ground.
 *
 * The expectation never comes from the collision under test. Cover is decided from the drawn ground (the
 * Landscape's height data); a covered point then needs the terrain's collision on that drawn ground and the
 * trace at the terrain, and an open point needs the trace at the roof. So a terrain collision sunk under a
 * covered roof, or raised over an open one, fails rather than redefining what the trace should meet.
 *
 * How many points take the covered branch is bounded too (IsBuriedWithinBound): a bundle whose roofs are
 * mostly under its ground would otherwise pass on the ground alone.
 */
struct FMantlePlaceRoofTraceLogic
{
	/** The height a hit may sit off the surface it is judged against, in centimetres. */
	static constexpr double SlackCm = 2.0;

	/**
	 * How far the terrain's collision may sit off the drawn ground at a point, in centimetres. Measured on a
	 * 3.0 bundle at its covered roof point: 0.0 cm for the Landscape and 0.3 cm for the terrain mesh. 2 cm is
	 * the same slack every hit here is held to, about seven times the larger measurement, and a hundredth of
	 * the 99.7 cm a relief-based bound allowed there.
	 */
	static constexpr double DrawnGroundToleranceCm = 2.0;

	/**
	 * The largest share of a level's roof points that may lie under the drawn ground. Measured: 1 of 622
	 * (0.16 %) on a 3.0 bundle and 1 of 667 (0.15 %) on a 1.15 one, each a single building on a slope steeper
	 * than its height over its length. 1 % is about six times that; a bundle past it has buildings set into
	 * its ground wholesale, which no roof check that excuses them can pass.
	 */
	static constexpr double MaxBuriedFraction = 0.01;

	/** The drawn ground under the point is over the roof by more than the slack. */
	static bool IsCovered(const FMantlePlaceRoofSample& Sample);

	static EMantlePlaceRoofTraceVerdict Judge(const FMantlePlaceRoofSample& Sample);

	/** The share of roof points under the drawn ground; 0 when there are none. */
	static double BuriedFraction(int32 Buried, int32 Open);

	/** At least one open roof point was judged, and the buried share is within MaxBuriedFraction. */
	static bool IsBuriedWithinBound(int32 Buried, int32 Open);
};
