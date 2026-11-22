// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceRoofTraceLogic.h"

namespace MantlePlaceRoofTraceLogicTest
{
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

	{
		FMantlePlaceRoofSample Sunk = Measured();
		Sunk.TerrainZ = Sunk.GroundZ - 30.0;
		Sunk.bHitBuildings = true;
		Sunk.bHitTerrain = false;
		Sunk.HitZ = Sunk.RoofZ;
		TestTrue(TEXT("a roof under the drawn ground is covered, wherever the terrain's collision is"), Logic::IsCovered(Sunk));
		TestEqual(TEXT("terrain collision sunk 30 cm under the drawn ground over a roof fails"), Logic::Judge(Sunk),
			EMantlePlaceRoofTraceVerdict::Missed);
		Sunk.bHitBuildings = false;
		Sunk.bHitTerrain = true;
		Sunk.HitZ = Sunk.TerrainZ;
		TestEqual(TEXT("and so does a trace that meets that sunk collision"), Logic::Judge(Sunk), EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Raised = Open();
		Raised.GroundZ = Raised.RoofZ - 1.0;
		Raised.GroundReliefCm = 40.0;
		Raised.TerrainZ = Raised.RoofZ + 30.0;
		Raised.bHitBuildings = false;
		Raised.bHitTerrain = true;
		Raised.HitZ = Raised.TerrainZ;
		TestFalse(TEXT("a roof over the drawn ground is open, whatever the terrain's collision does"), Logic::IsCovered(Raised));
		TestEqual(TEXT("terrain collision raised 30 cm over an open roof near the ground fails"), Logic::Judge(Raised),
			EMantlePlaceRoofTraceVerdict::Missed);
	}
	{
		FMantlePlaceRoofSample Off = Measured();
		Off.TerrainZ = Off.GroundZ + 5.0;
		Off.HitZ = Off.TerrainZ;
		TestEqual(TEXT("a covering terrain collision 5 cm off the drawn ground fails"), Logic::Judge(Off),
			EMantlePlaceRoofTraceVerdict::Missed);
		Off.TerrainZ = Off.GroundZ + 0.3;
		Off.HitZ = Off.TerrainZ;
		TestEqual(TEXT("and one 0.3 cm off it (the terrain mesh, measured) passes"), Logic::Judge(Off),
			EMantlePlaceRoofTraceVerdict::AtCoveringGround);
	}

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

	TestTrue(TEXT("1 buried point of 622 (the 3.0 bundle) is within the bound"), Logic::IsBuriedWithinBound(1, 621));
	TestTrue(TEXT("no buried point is within the bound"), Logic::IsBuriedWithinBound(0, 10));
	TestFalse(TEXT("roofs mostly under the ground fail"), Logic::IsBuriedWithinBound(500, 122));
	TestFalse(TEXT("2 % of the roof points buried fails"), Logic::IsBuriedWithinBound(2, 98));
	TestFalse(TEXT("no open roof point judged fails"), Logic::IsBuriedWithinBound(0, 0));
	TestFalse(TEXT("every roof point buried fails"), Logic::IsBuriedWithinBound(3, 0));
	TestEqual(TEXT("the buried fraction is buried over all roof points"), Logic::BuriedFraction(1, 621), 1.0 / 622.0);
	return true;
}

#endif
