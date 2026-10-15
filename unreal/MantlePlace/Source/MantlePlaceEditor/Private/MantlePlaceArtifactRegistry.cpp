// Copyright Mantle Place. All Rights Reserved.

// THE LIST. Every artifact type the importer knows is one entry in MakeAll, in import order. There is
// no discovery and no reflection scan: what imports, and in what order, is what this file says, and a
// reader sees all of it at once.
//
// Adding an artifact type means exactly: one handler file (MantlePlaceArtifact<Name>.cpp, which reads
// its block, declares its payloads and imports them), one sub-struct (its block in
// MantlePlaceManifestBlocks.h, held as one member of FMantlePlaceVaultManifest), and the declaration
// and entry below. Parse, the integrity chain and ImportVaultPackage do not change.

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

FHandlers MakeAll()
{
	FHandlers Handlers;
	// The drape builds its material BEFORE the landscape adopts it, and the log reads in this order.
	Handlers.Add(MakeDrape());
	Handlers.Add(MakeLandscape());
	Handlers.Add(MakeCoverageRasters());
	Handlers.Add(MakeTerrainMesh());
	Handlers.Add(MakeBuildings());
	Handlers.Add(MakeRoadSplines());
	Handlers.Add(MakeFoliagePoints());
	return Handlers;
}
}

FName MantlePlaceArtifactId::Landscape() { return FName(TEXT("landscape")); }
FName MantlePlaceArtifactId::TerrainMesh() { return FName(TEXT("terrain_mesh")); }

FMantlePlaceImportSelection FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode Mode)
{
	// Landscape and Mesh are alternatives for the terrain itself; everything else is content that
	// describes the ground and imports whenever the bundle ships it, whatever the Mode.
	FMantlePlaceImportSelection Selection;
	if (Mode == EMantlePlaceImportMode::Mesh)
	{
		Selection.Excluded.Add(MantlePlaceArtifactId::Landscape());
	}
	else if (Mode == EMantlePlaceImportMode::Landscape)
	{
		Selection.Excluded.Add(MantlePlaceArtifactId::TerrainMesh());
	}
	return Selection;
}
