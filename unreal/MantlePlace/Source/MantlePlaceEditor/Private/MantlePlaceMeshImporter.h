// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceTerrainCollisionLogic.h"

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
	 *
	 * The asset's collision is set here, not left to Interchange: its glTF default is one 18-sided
	 * convex hull around the whole mesh, a lid over a terrain or a site's massing that every trace and
	 * every body met instead of the ground (measured: 10 of 576 traces within a heightmap sample of the
	 * ground, the hull up to 16.7 m above it). The mesh collides as its own triangles instead: complex
	 * collision used as simple, the simple shapes removed.
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

	/**
	 * Set a placed mesh actor's collision explicitly: `Surface` blocks everything as the mesh's own
	 * triangles (its asset was given complex-as-simple collision by ImportMeshAsset), `None` turns it off.
	 * Import and ImportBuildings place every mesh as `Surface`; the terrain handler turns the overlay off
	 * in Both mode (and tags it), and Prepare for handover turns that mesh back on when it swaps the
	 * Landscape out (RestoreCollisionTurnedOff).
	 */
	void ApplyCollision(AStaticMeshActor* Actor, EMantlePlaceMeshCollision Collision);

	/**
	 * Turn collision back on for a mesh the import left without it (ApplyCollision `None` marks it with
	 * MantlePlaceImportNaming::CollisionOffTag), and only while it is still off: collision a person has
	 * set since, and every other mesh, is left alone. True when it turned collision on.
	 */
	bool RestoreCollisionTurnedOff(AStaticMeshActor* Actor);
}
