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
	ALandscape* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& HeightmapFile,
		UMaterialInstanceConstant* DrapeMaterial,
		const TArray<FMantlePlaceWeightPlane>& WeightPlanes,
		const FString& DestPackagePath,
		FString& OutError);

	bool SplitForWorldPartition(ALandscape* Landscape, int32 GridSizeInComponents);

	bool DecodeRgbaPng(
		const FString& File,
		const FString& InZipPath,
		FMantlePlaceRgbaImage& OutImage,
		FString& OutError);
}
