// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceCoverageRasterTypes.h"

namespace MantlePlaceFoliageScatterLogic
{
struct FRaster
{
	int32 Width = 0;
	int32 Height = 0;
	TArray<uint16> Samples;
	double FullScale = 65535.0;

	EMantlePlaceCoverageMapping Mapping = EMantlePlaceCoverageMapping::Identity;
	double MinValue = 0.0;
	double MaxValue = 0.0;
	bool bHasNodata = false;
	double NodataValue = 0.0;
	double TrueValue = 0.0;

	bool IsValid() const { return Width > 0 && Height > 0 && Samples.Num() == Width * Height; }
};

MANTLEPLACERUNTIME_API bool TexelAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, int32& OutColumn, int32& OutRow);

MANTLEPLACERUNTIME_API bool RawAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, uint16& OutSample);

MANTLEPLACERUNTIME_API bool DecodedScaleAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, double& OutValue);

struct FTreePoint
{
	FVector PositionCm = FVector::ZeroVector;
	bool bShrub = false;
	float HeightCm = 0.0f;
	float CrownRadiusCm = 0.0f;
	bool bGroundKnown = false;
};

struct FParams
{
	TArray<int32> ExcludedLandCoverClasses = { 50, 70, 80 };
	double MinCanopyHeightM = 0.5;
	double FullDensityCanopyHeightM = 3.0;
	double DensityScale = 1.0;
	int32 Seed = 0;
};

struct FInputs
{
	FVector2D AoiSizeCm = FVector2D::ZeroVector;
	FRaster CanopyHeight;
	FRaster WaterMask;
	FRaster LandCover;
};

enum class EVerdict : uint8
{
	Keep,
	OutsideAoi,
	OutsideCell,
	Water,
	ExcludedLandCover,
	NoCanopy,
	Thinned,
	NoHeight,
};

MANTLEPLACERUNTIME_API bool InCell(const FVector& PositionCm, const FBox& CellBounds);

MANTLEPLACERUNTIME_API double Draw(const FVector& PositionCm, int32 Seed, int32 Salt);

MANTLEPLACERUNTIME_API EVerdict Judge(const FTreePoint& Point, const FInputs& Inputs, const FParams& Params, const FBox* CellBounds);

MANTLEPLACERUNTIME_API double TargetHeightCm(const FTreePoint& Point, const FInputs& Inputs);

MANTLEPLACERUNTIME_API FTransform InstanceTransform(const FTreePoint& Point, double HeightCm, double GroundZCm, const FBox& MeshBounds, int32 Seed);

MANTLEPLACERUNTIME_API int32 PickMesh(const FVector& PositionCm, int32 MeshCount, int32 Seed);
}
