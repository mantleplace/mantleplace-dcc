// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceCoverageRasterTypes.h"

/**
 * The decisions the PCG foliage scatter makes, pure and headless-testable: which tree point
 * survives, how tall and wide its instance is, and which mesh it gets. The PCG element
 * (MantlePlaceTreeScatterSettings.cpp) reads the table and the textures and executes; this decides.
 *
 * Everything here APPLIES published values. The point, its height and crown come from the
 * tree-points table; the canopy height, the water mask and the land-cover class come from the
 * coverage rasters, decoded by the value mapping the platform published with them. What is chosen
 * here, and only here, is the aesthetic: how strongly a thin canopy thins the scatter, which land
 * cover keeps trees off it, which mesh a point gets and how it is turned.
 */
namespace MantlePlaceFoliageScatterLogic
{
/** One coverage raster as raw samples, in its texture's own order (NorthUpRowMajor). */
struct FRaster
{
	int32 Width = 0;
	int32 Height = 0;
	/** Width * Height raw samples, row 0 the northern edge, columns west to east. */
	TArray<uint16> Samples;
	/** The sample the encoding's full scale is: 255 for 8-bit, 65535 for 16-bit. */
	double FullScale = 65535.0;

	EMantlePlaceCoverageMapping Mapping = EMantlePlaceCoverageMapping::Identity;
	double MinValue = 0.0;
	double MaxValue = 0.0;
	bool bHasNodata = false;
	double NodataValue = 0.0;
	double TrueValue = 0.0;

	bool IsValid() const { return Width > 0 && Height > 0 && Samples.Num() == Width * Height; }
};

/**
 * The texel under a local-frame position. The raster spans the AOI, centred on the origin, whose
 * size `AoiSizeCm` is (North span, East span) as FMantlePlaceVaultManifest::GetAoiSizeUeCm gives
 * it; North is +X and East is +Y, and the texture is north-up (row 0 the northern edge). False
 * outside the AOI or for an invalid raster.
 */
MANTLEPLACERUNTIME_API bool TexelAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, int32& OutColumn, int32& OutRow);

/** The raw sample under a position, or false where TexelAt is. */
MANTLEPLACERUNTIME_API bool RawAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, uint16& OutSample);

/**
 * A Scale raster's value under a position, decoded by its published mapping: MinValue at sample 0,
 * MaxValue at the full-scale sample. False for nodata, outside the AOI, or a raster that is not a
 * Scale (a Legend's class codes are not a ramp).
 */
MANTLEPLACERUNTIME_API bool DecodedScaleAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, double& OutValue);

/** The point a scatter is asked about: the table's columns it reads, by the names in the contract. */
struct FTreePoint
{
	FVector PositionCm = FVector::ZeroVector;
	bool bShrub = false;
	float HeightCm = 0.0f;
	float CrownRadiusCm = 0.0f;
	bool bGroundKnown = false;
};

/** The aesthetic knobs. Defaults are the shipped look; the graph's node exposes every one. */
struct FParams
{
	/** Land-cover class codes a tree never stands on. Defaults: built-up, snow and ice, permanent water. */
	TArray<int32> ExcludedLandCoverClasses = { 50, 70, 80 };
	/** At or below this canopy height (m) a TREE point is dropped: the canopy raster sees no tree there. */
	double MinCanopyHeightM = 0.5;
	/** At or above this canopy height (m) every tree point is kept; between the two, kept in proportion. */
	double FullDensityCanopyHeightM = 3.0;
	/** Scales the whole scatter's density, 0 to 1. */
	double DensityScale = 1.0;
	/** Seeds the deterministic thinning, mesh choice and yaw. */
	int32 Seed = 0;
};

/** The rasters a scatter reads. Any may be invalid (not shipped); a missing one filters nothing. */
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

/** A point is in a partition cell when it is in [Min, Max) on X and Y: a point on a border is in one cell, never two. */
MANTLEPLACERUNTIME_API bool InCell(const FVector& PositionCm, const FBox& CellBounds);

/** A deterministic 0..1 draw for one point: the same point, seed and salt always draw the same. */
MANTLEPLACERUNTIME_API double Draw(const FVector& PositionCm, int32 Seed, int32 Salt);

/**
 * Whether a point is scattered, and if not, why. In order: outside the AOI, outside the cell (when
 * a cell is given), on water (the mask is set), on an excluded land-cover class, then -- trees
 * only -- the canopy: at or below MinCanopyHeightM dropped, between it and FullDensityCanopyHeightM
 * kept in proportion; nodata or no canopy raster leaves the table's point standing. DensityScale
 * thins every point last.
 */
MANTLEPLACERUNTIME_API EVerdict Judge(const FTreePoint& Point, const FInputs& Inputs, const FParams& Params, const FBox* CellBounds);

/**
 * The instance's height in cm: the table's published HeightCm, and where the row has none, the
 * decoded canopy height under it. Zero when neither publishes one, which Judge's caller drops.
 */
MANTLEPLACERUNTIME_API double TargetHeightCm(const FTreePoint& Point, const FInputs& Inputs);

/**
 * The instance transform for a mesh whose local bounds are `MeshBounds`: scaled so its height is
 * `HeightCm` and its width the crown's diameter (uniform when the row publishes no crown), its
 * base on `GroundZCm`, and turned about Z by a deterministic yaw.
 */
MANTLEPLACERUNTIME_API FTransform InstanceTransform(const FTreePoint& Point, double HeightCm, double GroundZCm, const FBox& MeshBounds, int32 Seed);

/** Which of `MeshCount` meshes a point gets: deterministic, INDEX_NONE when there are none. */
MANTLEPLACERUNTIME_API int32 PickMesh(const FVector& PositionCm, int32 MeshCount, int32 Seed);
}
