// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
struct FMantlePlaceRgbaImage;
struct FMantlePlaceWeightPlane;
class UWorld;
class ALandscape;
class UMaterialInstanceConstant;

namespace MantlePlaceLandscapeImporter
{
	/**
	 * Build a native Landscape from the bundle's 16-bit grayscale heightmap PNG (already
	 * extracted to HeightmapFile on disk). Decodes the PNG, orients rows so North maps to
	 * +Y, and applies the manifest's exact scale/location. Returns the spawned ALandscape,
	 * or nullptr with OutError on failure.
	 *
	 * DrapeMaterial (optional) is assigned to LandscapeMaterial BEFORE ALandscape::Import builds
	 * the components, so the landscape adopts the imagery drape at creation time. Assigning it
	 * after creation leaves the components on the default material until re-applied to a finalized
	 * landscape, so the material must be in hand before this call (pass nullptr for no drape).
	 *
	 * WeightPlanes (may be empty) are the `unreal.landscape_layers.material_weights` planes built by
	 * FMantlePlaceLandscapeWeightsLogic. One ULandscapeLayerInfoObject per plane is created under
	 * DestPackagePath and handed to Import() alongside the height data, so the weights land in
	 * the Landscape's own weightmaps at creation. Painting them at creation is the only cheap moment:
	 * ALandscape::Import allocates the weightmap textures, and adding a layer afterwards means
	 * reallocating them per component.
	 */
	ALandscape* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& HeightmapFile,
		UMaterialInstanceConstant* DrapeMaterial,
		const TArray<FMantlePlaceWeightPlane>& WeightPlanes,
		const FString& DestPackagePath,
		FString& OutError);

	/**
	 * In a World Partition level, split a freshly imported landscape into ALandscapeStreamingProxy
	 * actors of GridSizeInComponents components per axis, the way the engine's New Landscape tool
	 * does (ULandscapeSubsystem::ChangeGridSize, after Import). Returns true when it split; false,
	 * having touched nothing, in a non-partitioned level, which is how that level's import stays
	 * exactly what it was. Import() calls it with the project's grid-size setting; the World
	 * Partition test calls it directly.
	 *
	 * The proxies inherit the parent's LandscapeMaterial (SynchronizeSharedProperties) and their
	 * components keep the material they were built with; the parent keeps no components at all.
	 */
	bool SplitForWorldPartition(ALandscape* Landscape, int32 GridSizeInComponents);

	/**
	 * Decode an 8-bit RGBA PNG (already extracted to disk) into an FMantlePlaceRgbaImage tagged with
	 * the in-zip path it came from. The impure half of the weights path; the pure half decides what
	 * the channels mean.
	 */
	bool DecodeRgbaPng(
		const FString& File,
		const FString& InZipPath,
		FMantlePlaceRgbaImage& OutImage,
		FString& OutError);
}
