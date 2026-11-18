// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceRoofTraceLogic.h"

// How the live terrain-collision test judges one trace down onto the building massing, asserted headless.
// The motive is a real 3.0 bundle: a 49 m by 16 m building on a slope whose ground rises 5 m along it, so
// at its uphill end the drawn ground lies 0.22 m over its flat roof. A default trace there correctly meets
// the ground first, and the test, which expected the roof at every roof point, failed on it.

namespace MantlePlaceRoofTraceLogicTest
{
/** The sample measured on that bundle, in centimetres: the roof 21.6 cm under the Landscape. */
FMantlePlaceRoofSample Measured()
{
	FMantlePlaceRoofSample Sample;
	Sample.RoofZ = 191319.975;
	Sample.bTerrain = true;
	Sample.TerrainZ = 191341.621;
	Sample.GroundZ = 191341.6;
	Sample.GroundReliefCm = 40.0;
	Sample.bHit = true;
	Sample.bHitBuildings = false;
	Sample.bHitTerrain = true;
	Sample.HitZ = 191341.621;
	return Sample;
}

/** An open roof 5 m over the ground, hit where it is. */
FMantlePlaceRoofSample Open()
{
	FMantlePlaceRoofSample Sample;
	Sample.RoofZ = 1500.0;
	Sample.bTerrain = true;
	Sample.TerrainZ = 1000.0;
	Sample.GroundZ = 1000.0;
	Sample.GroundReliefCm = 10.0;
	Sample.bHit = true;
	Sample.bHitBuildings = true;
	Sample.bHitTerrain = false;
	Sample.HitZ = 1500.0;
	return Sample;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceRoofTraceLogicTest,
	"MantlePlace.Import.RoofTraceLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceRoofTraceLogicTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceRoofTraceLogicTest;
	using Logic = FMantlePlaceRoofTraceLogic;

	// --- An open roof: the trace must meet the buildings at the roof. ---------------------------------
	TestFalse(TEXT("a roof over the ground is open"), Logic::IsCovered(Open()));
	TestEqual(TEXT("an open roof hit at the roof passes"), Logic::Judge(Open()), EMantlePlaceRoofTraceVerdict::AtRoof);
	{
		FMantlePlaceRoofSample Hull = Open();
		Hull.HitZ = Hull.RoofZ + 50.0;
		TestEqual(TEXT("an open roof hit on a hull 50 cm above it fails"), Logic::Judge(Hull), EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Through = Open();
		Through.bHitBuildings = false;
		Through.bHitTerrain = true;
		Through.HitZ = Through.TerrainZ;
		TestEqual(TEXT("an open roof the trace falls through to the ground fails"), Logic::Judge(Through), EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Nothing = Open();
		Nothing.bHit = false;
		Nothing.bHitBuildings = false;
		TestEqual(TEXT("an open roof the trace misses entirely fails"), Logic::Judge(Nothing), EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample OffTerrain = Open();
		OffTerrain.bTerrain = false;
		TestFalse(TEXT("a roof beyond the terrain's edge is open"), Logic::IsCovered(OffTerrain));
		TestEqual(TEXT("and is judged as a roof"), Logic::Judge(OffTerrain), EMantlePlaceRoofTraceVerdict::AtRoof);
	}

	// --- The measured case: the ground drawn over the roof. -------------------------------------------
	TestTrue(TEXT("the measured roof, 21.6 cm under the drawn ground, is covered"), Logic::IsCovered(Measured()));
	TestEqual(TEXT("and the trace meeting the ground over it passes"), Logic::Judge(Measured()),
		EMantlePlaceRoofTraceVerdict::AtCoveringGround);
	{
		FMantlePlaceRoofSample Buildings = Measured();
		Buildings.bHitBuildings = true;
		Buildings.bHitTerrain = false;
		Buildings.HitZ = Buildings.RoofZ;
		TestEqual(TEXT("a trace that meets a covered roof through the ground fails: the terrain does not collide there"),
			Logic::Judge(Buildings), EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Hull = Measured();
		Hull.TerrainZ = Hull.GroundZ + 100.0;
		Hull.HitZ = Hull.TerrainZ;
		TestEqual(TEXT("terrain collision a metre over the drawn ground does not excuse a roof"), Logic::Judge(Hull),
			EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Above = Measured();
		Above.HitZ = Above.TerrainZ + 10.0;
		TestEqual(TEXT("a covered roof hit 10 cm over the terrain's own collision fails"), Logic::Judge(Above),
			EMantlePlaceRoofTraceVerdict::Missed);
	}

	// --- A tie: the ground level with the roof, within the slack. -------------------------------------
	{
		FMantlePlaceRoofSample Tie = Open();
		Tie.TerrainZ = Tie.RoofZ + 1.0;
		Tie.GroundZ = Tie.TerrainZ;
		TestFalse(TEXT("ground within the slack of the roof does not cover it"), Logic::IsCovered(Tie));
		Tie.bHitBuildings = false;
		Tie.bHitTerrain = true;
		Tie.HitZ = Tie.TerrainZ;
		TestEqual(TEXT("and a trace meeting that ground is at the roof's height"), Logic::Judge(Tie), EMantlePlaceRoofTraceVerdict::AtRoof);
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
