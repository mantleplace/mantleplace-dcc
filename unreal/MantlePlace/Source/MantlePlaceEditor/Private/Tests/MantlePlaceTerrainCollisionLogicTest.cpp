// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceTerrainCollisionLogic.h"

// Which imported terrain collides: one ground, never two. The live measurement of the whole import is
// MantlePlace.Import.LiveTerrainCollision; this pins the decision on its own.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceTerrainCollisionLogicTest,
	"MantlePlace.Import.TerrainCollisionLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTerrainCollisionLogicTest::RunTest(const FString& Parameters)
{
	TestEqual(TEXT("a terrain mesh imported alone is the ground: it collides as its own surface"),
		MantlePlaceTerrainCollisionLogic::ForTerrainMesh(/*bLandscapeBuilt*/ false), EMantlePlaceMeshCollision::Surface);
	TestEqual(TEXT("a terrain mesh over a Landscape built by the same import (Both mode) does not collide"),
		MantlePlaceTerrainCollisionLogic::ForTerrainMesh(/*bLandscapeBuilt*/ true), EMantlePlaceMeshCollision::None);
	TestEqual(TEXT("building massing collides as its own surface"),
		MantlePlaceTerrainCollisionLogic::ForBuildings(), EMantlePlaceMeshCollision::Surface);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
