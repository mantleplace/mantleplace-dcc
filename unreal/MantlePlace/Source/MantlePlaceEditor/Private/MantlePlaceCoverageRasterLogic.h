// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Engine/TextureDefines.h"
#include "MantlePlaceCoverageRasterTypes.h"

struct FMantlePlaceRasterValueMapping;

/** The texture settings one coverage raster needs, derived rather than left at import defaults. */
struct FMantlePlaceCoverageTextureSettings
{
	TextureCompressionSettings Compression = TC_Grayscale;
	TextureMipGenSettings MipGen = TMGS_NoMipmaps;
	TextureFilter Filter = TF_Default;
	bool bSRGB = false;
	bool bVirtualTextureStreaming = false;
};

/**
 * Pure (engine-/IO-free) logic for the seven `unreal.landscape_layers` sub-blocks that are NOT
 * `material_weights`: which mapping a raster carries, and what texture settings its pixels need to
 * survive import as data. Deterministic and headless-testable; the shim owns the impure parts
 * (extract, UAssetImportTask, asset mutation, UAssetUserData attachment).
 *
 * Everything here exists because UE's texture import defaults are tuned for colour, and every one
 * of those defaults is wrong for a raster whose pixels are metres, degrees or class codes:
 *
 *  - **The engine's landed format is the authority, not the manifest's `encoding`.** `encoding` says
 *    what the ETL wrote; `Source.GetFormat()` says what the engine actually produced. They can
 *    disagree — a 16-bit gray PNG carrying a `tRNS` chunk is decoded as RGBA, and applying
 *    TC_Grayscale to that yields a silently 8-bit texture that looks entirely fine. So the settings
 *    are keyed off the landed format, and a contradiction is refused rather than papered over.
 *  - **TC_Grayscale is "G8/16 from source R", not an 8-bit format.** It resolves to PF_G16 exactly
 *    when the source is TSF_G16, which makes it the lossless AND minimal-memory choice for the
 *    16-bit channels. TC_HalfFloat would be the lossy one: fp16's 10-bit mantissa cannot represent
 *    uint16 values above 2048.
 */
struct FMantlePlaceCoverageRasterLogic
{
	/**
	 * True for every landscape layer that is a coverage raster — i.e. all of them except
	 * `material_weights`, which is applied as weightmap layers and changes what the Landscape IS.
	 */
	static bool IsCoverageRaster(const FString& LayerName);

	/**
	 * Which mapping a raster carries, from its `encoding` and the shape of its `value_mapping`.
	 * Fails closed when the mapping is missing the fields its kind requires — a Legend with no
	 * `classes[]` or a Scale with min == max is not a raster we can hand a consumer a meaning for,
	 * and guessing one is worse than skipping the layer.
	 */
	static bool ResolveMappingKind(
	    const FString& Encoding,
	    const FMantlePlaceRasterValueMapping& Mapping,
	    EMantlePlaceCoverageMapping& OutKind,
	    FString& OutError);

	/**
	 * Texture settings for a raster of `Encoding` that the engine actually landed as `LandedFormat`.
	 * Fails closed on a contradiction between the two, naming both — that mismatch is the one
	 * failure here that would otherwise produce a plausible-looking wrong answer instead of a
	 * missing asset.
	 */
	static bool ResolveTextureSettings(
	    const FString& Encoding,
	    ETextureSourceFormat LandedFormat,
	    FMantlePlaceCoverageTextureSettings& OutSettings,
	    FString& OutError);
};
