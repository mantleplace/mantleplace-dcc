// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceLandscapeLayer;

/** One decoded UE-ready RGBA PNG, as the importer read it off disk. Row 0 is the PNG's first row. */
struct FMantlePlaceRgbaImage
{
	FString Path;          // the in-zip path it was decoded from — matched against ue_ready[].path
	int32 Width = 0;
	int32 Height = 0;
	TArray<uint8> Pixels;  // Width*Height*4, RGBA interleaved
};

/** Where one material's weights live: which ue_ready image, and which RGBA channel of it. */
struct FMantlePlaceWeightBand
{
	FString Material;
	FString ImagePath;
	int32 Channel = 0; // 0=R 1=G 2=B 3=A
};

/** One landscape weightmap layer, sized to the landscape grid and in the landscape's row order. */
struct FMantlePlaceWeightPlane
{
	FString Material;
	TArray<uint8> Data; // Size*Size, X fastest, row 0 = the SOUTH edge (ALandscape::Import order)
};

/**
 * Pure (engine-/IO-free) logic for `unreal.landscape_layers.material_weights`: the ETL's two 4-band
 * RGBA PNGs -> one uint8 weight plane per material, on the Landscape's post grid. Deterministic and
 * headless-testable under -nullrhi; the importer shim owns the impure parts (zip read, PNG decode,
 * ULandscapeLayerInfoObject creation, ALandscape::Import).
 *
 * Two facts from the contract shape everything here:
 *
 *  - **The band legend is data, not a convention.** `materials[]` is the frozen band order and each
 *    `ue_ready[].value_mapping.bands` names that PNG's four channels. Eight materials do not fit one
 *    RGBA texture, so the pipeline ships two halves; which half a material is in, and in which
 *    channel, is read from the manifest and never assumed from the file name (`HPS-32`).
 *  - **The weight rasters are coarser than the Landscape, over the same ground.**
 *    MaterialWeights.tif is built on the DEM's own grid and transform (the ETL reprojects
 *    WorldCover to `dem_transform`), while Heightmap.png is that same AOI resampled to a
 *    Landscape-friendly post count. The two therefore cover an IDENTICAL AOI-UTM window at
 *    different sample counts, and the manifest's own numbers say so: a bundle with
 *    `native_gsd_m: 10`, `landscape_post_spacing_m: 2.8373`, `resolution: 505` has an AOI
 *    504 * 2.8373 = 1430 m wide, which is exactly the 143 columns of its 143x142 weight raster at
 *    10 m. So sampling is a corner-to-corner nearest neighbour over normalised grid coordinates —
 *    no extent arithmetic (the layers publish none), and no assumption that the grids share a
 *    resolution. Nearest neighbour, not bilinear: these are categorical coverage fractions, and
 *    interpolating across a class boundary invents a blend the source never claimed.
 */
struct FMantlePlaceLandscapeWeightsLogic
{
	/**
	 * Resolve `materials[]` to (image, channel) pairs via each `ue_ready[].value_mapping.bands`,
	 * in the legend's own band order. Fails closed (false + OutError) when the layer carries no
	 * legend, no RGBA companions, or a material no companion names — a half-resolved legend would
	 * paint weights into the wrong layer, which is worse than not painting them.
	 */
	static bool ResolveBands(
	    const FMantlePlaceLandscapeLayer& Layer,
	    TArray<FMantlePlaceWeightBand>& OutBands,
	    FString& OutError);

	/**
	 * Sample the decoded companions onto a Size x Size Landscape grid, one plane per material.
	 * `bRow0IsNorth` is the heightmap's own row convention, which the weight PNGs share (both are
	 * north-up rasters); the Landscape's row 0 is its south edge, so rows are flipped to match.
	 * Fails closed when a companion is missing, has dimensions the manifest did not declare, or
	 * carries too few bytes to be the RGBA image it claims to be.
	 */
	static bool BuildWeightPlanes(
	    const FMantlePlaceLandscapeLayer& Layer,
	    const TArray<FMantlePlaceRgbaImage>& Images,
	    int32 Size,
	    bool bRow0IsNorth,
	    TArray<FMantlePlaceWeightPlane>& OutPlanes,
	    FString& OutError);

	/**
	 * Nearest-neighbour source index for one axis: post `Post` of `PostCount` maps to a pixel of
	 * `PixelCount` covering the same span, corner to corner. Post 0 lands on pixel 0 and the last
	 * post on the last pixel, whichever way the two counts differ.
	 */
	static int32 SampleIndex(int32 Post, int32 PostCount, int32 PixelCount);
};
