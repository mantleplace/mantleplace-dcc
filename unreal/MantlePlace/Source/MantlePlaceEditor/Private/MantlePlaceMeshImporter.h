// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
class UWorld;
class AStaticMeshActor;

namespace MantlePlaceMeshImporter
{
	/**
	 * Import the bundle's Terrain.glb via Interchange (auto Y-up -> Z-up) and spawn a
	 * StaticMeshActor at the AOI centroid / true elevation so it overlays the Landscape.
	 * Returns the spawned actor, or nullptr with OutError on failure.
	 */
	AStaticMeshActor* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		bool bEnableNanite,
		FString& OutError);

	/**
	 * Import the bundle's Buildings.glb (extruded massing) via Interchange and spawn a StaticMeshActor
	 * in the same Local Projected Frame as the terrain (GetMeshLocation), so buildings rest on the
	 * ground. Nanite is left off (massing is trivially small) and no imagery drape is applied.
	 * Returns the spawned actor, or nullptr with OutError on failure.
	 */
	AStaticMeshActor* ImportBuildings(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		FString& OutError);
}
