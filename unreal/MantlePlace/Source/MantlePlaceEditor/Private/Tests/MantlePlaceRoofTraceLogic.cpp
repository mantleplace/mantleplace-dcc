// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceRoofTraceLogic.h"

namespace
{
bool CollidesWhereDrawn(const FMantlePlaceRoofSample& Sample)
{
	return Sample.bTerrain && FMath::Abs(Sample.TerrainZ - Sample.GroundZ) <= FMantlePlaceRoofTraceLogic::DrawnGroundToleranceCm;
}
}

bool FMantlePlaceRoofTraceLogic::IsCovered(const FMantlePlaceRoofSample& Sample)
{
	return Sample.GroundZ > Sample.RoofZ + SlackCm;
}

double FMantlePlaceRoofTraceLogic::BuriedFraction(int32 Buried, int32 Open)
{
	const int32 Total = Buried + Open;
	return Total > 0 ? double(Buried) / double(Total) : 0.0;
}

bool FMantlePlaceRoofTraceLogic::IsBuriedWithinBound(int32 Buried, int32 Open)
{
	return Open > 0 && BuriedFraction(Buried, Open) <= MaxBuriedFraction;
}

EMantlePlaceRoofTraceVerdict FMantlePlaceRoofTraceLogic::Judge(const FMantlePlaceRoofSample& Sample)
{
	if (!Sample.bHit)
	{
		return EMantlePlaceRoofTraceVerdict::Missed;
	}
	if (IsCovered(Sample))
	{
		return CollidesWhereDrawn(Sample) && Sample.bHitTerrain && FMath::Abs(Sample.HitZ - Sample.TerrainZ) <= SlackCm
			? EMantlePlaceRoofTraceVerdict::AtCoveringGround
			: EMantlePlaceRoofTraceVerdict::Missed;
	}
	const bool bTie = FMath::Abs(Sample.GroundZ - Sample.RoofZ) <= SlackCm && CollidesWhereDrawn(Sample);
	const bool bSurface = Sample.bHitBuildings || (bTie && Sample.bHitTerrain);
	return bSurface && FMath::Abs(Sample.HitZ - Sample.RoofZ) <= SlackCm
		? EMantlePlaceRoofTraceVerdict::AtRoof
		: EMantlePlaceRoofTraceVerdict::Missed;
}
