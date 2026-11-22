// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceRoofSample
{
	double RoofZ = 0.0;
	bool bTerrain = false;
	double TerrainZ = 0.0;
	double GroundZ = 0.0;
	double GroundReliefCm = 0.0;
	bool bHit = false;
	bool bHitBuildings = false;
	bool bHitTerrain = false;
	double HitZ = 0.0;
};

enum class EMantlePlaceRoofTraceVerdict : uint8
{
	AtRoof,
	AtCoveringGround,
	Missed,
};

struct FMantlePlaceRoofTraceLogic
{
	static constexpr double SlackCm = 2.0;

	static constexpr double DrawnGroundToleranceCm = 2.0;

	static constexpr double MaxBuriedFraction = 0.01;

	static bool IsCovered(const FMantlePlaceRoofSample& Sample);

	static EMantlePlaceRoofTraceVerdict Judge(const FMantlePlaceRoofSample& Sample);

	static double BuriedFraction(int32 Buried, int32 Open);

	static bool IsBuriedWithinBound(int32 Buried, int32 Open);
};
