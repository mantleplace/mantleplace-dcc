// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
class UWorld;
class AStaticMeshActor;
class UStaticMesh;

namespace MantlePlaceMeshImporter
{
	/**
	 * Import a .glb via Interchange (auto Y-up -> Z-up) and return its UStaticMesh. Creates ASSETS
	 * ONLY -- no actor, nothing that touches the level.
	 *
	 * Split out from the spawn half, and called BEFORE the import's undo transaction opens, because
	 * a glTF brings its own textures and materials along and those arrive named by the source file
	 * (`Terrain_texture_0`). Naming them to the project standard means a rename, and a rename inside
	 * the transaction takes the whole import off the undo stack -- see
	 * MantlePlaceImportNaming::ImportNameFor. DestinationName covers the mesh itself but cannot
	 * reach what is embedded beside it, so the fix is to do this work before there is a transaction
	 * to lose. Measured 2026-08-30: importing Terrain.glb inside the transaction purged a
	 * 500-actor import, and the level then ignored Ctrl+Z entirely.
	 */
	UStaticMesh* ImportMeshAsset(
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		bool bEnableNanite,
		FString& OutError);

	/**
	 * Spawn a StaticMeshActor for an already-imported terrain mesh at the AOI centroid / true
	 * elevation so it overlays the Landscape. Returns the spawned actor, or nullptr with OutError.
	 */
	AStaticMeshActor* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError);

	/**
	 * Spawn a StaticMeshActor for already-imported building massing, in the same Local Projected
	 * Frame as the terrain (GetMeshLocation), so buildings rest on the ground. Nanite is left off
	 * (massing is trivially small) and no imagery drape is applied.
	 * Returns the spawned actor, or nullptr with OutError on failure.
	 */
	AStaticMeshActor* ImportBuildings(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError);
}
