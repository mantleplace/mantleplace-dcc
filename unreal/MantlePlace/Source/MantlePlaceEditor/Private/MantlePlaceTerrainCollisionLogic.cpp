// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTerrainCollisionLogic.h"

namespace MantlePlaceTerrainCollisionLogic
{
	EMantlePlaceMeshCollision ForTerrainMesh(bool bLandscapeBuilt)
	{
		return bLandscapeBuilt ? EMantlePlaceMeshCollision::None : EMantlePlaceMeshCollision::Surface;
	}

	EMantlePlaceMeshCollision ForBuildings()
	{
		return EMantlePlaceMeshCollision::Surface;
	}
}
