// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceRoofTraceLogic.h"

bool FMantlePlaceRoofTraceLogic::IsCovered(const FMantlePlaceRoofSample& Sample)
{
	return Sample.bTerrain && Sample.TerrainZ > Sample.RoofZ + SlackCm;
}

EMantlePlaceRoofTraceVerdict FMantlePlaceRoofTraceLogic::Judge(const FMantlePlaceRoofSample& Sample)
{
	if (!Sample.bHit)
	{
		return EMantlePlaceRoofTraceVerdict::Missed;
	}
	if (IsCovered(Sample))
	{
		// Only the drawn ground may cover a roof: the terrain's collision there sits on the rendered ground,
		// as the terrain grid of the same test holds it, and the trace met that terrain where it collides.
		const bool bDrawn = FMath::Abs(Sample.TerrainZ - Sample.GroundZ) <= Sample.GroundReliefCm + SlackCm;
		return bDrawn && Sample.bHitTerrain && FMath::Abs(Sample.HitZ - Sample.TerrainZ) <= SlackCm
			? EMantlePlaceRoofTraceVerdict::AtCoveringGround
			: EMantlePlaceRoofTraceVerdict::Missed;
	}
	// An open roof. Ground level with it, within the slack, may be what the trace meets first: either
	// surface is at the roof's height.
	const bool bTie = Sample.bTerrain && FMath::Abs(Sample.TerrainZ - Sample.RoofZ) <= SlackCm;
	const bool bSurface = Sample.bHitBuildings || (bTie && Sample.bHitTerrain);
	return bSurface && FMath::Abs(Sample.HitZ - Sample.RoofZ) <= SlackCm
		? EMantlePlaceRoofTraceVerdict::AtRoof
		: EMantlePlaceRoofTraceVerdict::Missed;
}
