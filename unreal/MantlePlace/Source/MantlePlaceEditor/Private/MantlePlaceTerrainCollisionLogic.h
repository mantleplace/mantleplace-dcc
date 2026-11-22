// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

enum class EMantlePlaceMeshCollision : uint8
{
	Surface,
	None,
};

namespace MantlePlaceTerrainCollisionLogic
{
	EMantlePlaceMeshCollision ForTerrainMesh(bool bLandscapeBuilt);

	EMantlePlaceMeshCollision ForBuildings();
}
