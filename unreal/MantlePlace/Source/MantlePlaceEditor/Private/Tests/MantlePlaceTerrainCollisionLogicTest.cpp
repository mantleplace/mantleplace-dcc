// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceTerrainCollisionLogic.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceTerrainCollisionLogicTest,
	"MantlePlace.Import.TerrainCollisionLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTerrainCollisionLogicTest::RunTest(const FString& Parameters)
{
	TestEqual(TEXT("a terrain mesh imported alone is the ground: it collides as its own surface"),
		MantlePlaceTerrainCollisionLogic::ForTerrainMesh(false), EMantlePlaceMeshCollision::Surface);
	TestEqual(TEXT("a terrain mesh over a Landscape built by the same import (Both mode) does not collide"),
		MantlePlaceTerrainCollisionLogic::ForTerrainMesh(true), EMantlePlaceMeshCollision::None);
	TestEqual(TEXT("building massing collides as its own surface"),
		MantlePlaceTerrainCollisionLogic::ForBuildings(), EMantlePlaceMeshCollision::Surface);

	int32 LandscapeAt = INDEX_NONE;
	int32 TerrainMeshAt = INDEX_NONE;
	const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
	for (int32 Index = 0; Index < Handlers.Num(); ++Index)
	{
		const FName Id = Handlers[Index]->Id();
		LandscapeAt = Id == MantlePlaceArtifactId::Landscape() ? Index : LandscapeAt;
		TerrainMeshAt = Id == MantlePlaceArtifactId::TerrainMesh() ? Index : TerrainMeshAt;
	}
	TestTrue(TEXT("the registry has a Landscape handler"), LandscapeAt != INDEX_NONE);
	TestTrue(TEXT("and a terrain mesh handler"), TerrainMeshAt != INDEX_NONE);
	TestTrue(TEXT("the Landscape imports before the terrain mesh, which reads whether it was built"), LandscapeAt < TerrainMeshAt);
	return true;
}

#endif
