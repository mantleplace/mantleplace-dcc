// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeDrape();
TUniquePtr<IMantlePlaceArtifactHandler> MakeLandscape();
TUniquePtr<IMantlePlaceArtifactHandler> MakeCoverageRasters();
TUniquePtr<IMantlePlaceArtifactHandler> MakeTerrainMesh();
TUniquePtr<IMantlePlaceArtifactHandler> MakeBuildings();
TUniquePtr<IMantlePlaceArtifactHandler> MakeRoadSplines();
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliagePoints();
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliageScatter();
TUniquePtr<IMantlePlaceArtifactHandler> MakePublicRecords();

FHandlers MakeAll()
{
	FHandlers Handlers;
	Handlers.Add(MakeDrape());
	Handlers.Add(MakeLandscape());
	Handlers.Add(MakeCoverageRasters());
	Handlers.Add(MakeTerrainMesh());
	Handlers.Add(MakeBuildings());
	Handlers.Add(MakeRoadSplines());
	Handlers.Add(MakeFoliagePoints());
	Handlers.Add(MakeFoliageScatter());
	Handlers.Add(MakePublicRecords());
	return Handlers;
}
}

FName MantlePlaceArtifactId::Landscape() { return FName(TEXT("landscape")); }
FName MantlePlaceArtifactId::TerrainMesh() { return FName(TEXT("terrain_mesh")); }
FName MantlePlaceArtifactId::FoliageScatter() { return FName(TEXT("foliage_scatter")); }

FMantlePlaceImportSelection FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode Mode, bool bFoliageScatter)
{
	FMantlePlaceImportSelection Selection;
	if (Mode == EMantlePlaceImportMode::Mesh)
	{
		Selection.Excluded.Add(MantlePlaceArtifactId::Landscape());
	}
	else if (Mode == EMantlePlaceImportMode::Landscape)
	{
		Selection.Excluded.Add(MantlePlaceArtifactId::TerrainMesh());
	}
	if (!bFoliageScatter)
	{
		Selection.Excluded.Add(MantlePlaceArtifactId::FoliageScatter());
	}
	return Selection;
}
