// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceLandscapeLayer;
struct FMantlePlaceUeReadyRaster;
class UTexture2D;

/**
 * Imports one coverage raster — a `unreal.landscape_layers` sub-block other than `material_weights`
 * — as a UTexture2D carrying its meaning and provenance in a UMantlePlaceCoverageRasterData.
 *
 * The contract's obligation is that published data is REACHABLE WITH MEANING. Deciding what water
 * or snow look like, or what slope and ndvi drive, is product design and is deliberately not here:
 * these textures are addressable by any material or PCG graph, and nothing in this plugin samples
 * them. That is not the "published and ignored" defect — nothing is
 * hidden, and no pointer goes unread.
 */
namespace MantlePlaceCoverageRasters
{
	/**
	 * Import `DiskFile` (already extracted and hash-verified) as a coverage-raster texture under
	 * `DestPackagePath`/CoverageRasters. Returns nullptr with OutError when the raster cannot be
	 * imported as data — including when the engine's landed format contradicts the manifest's
	 * declared encoding, which is refused rather than imported at the wrong precision.
	 */
	UTexture2D* ImportRaster(
		const FMantlePlaceLandscapeLayer& Layer,
		const FMantlePlaceUeReadyRaster& Raster,
		const FString& DiskFile,
		const FString& JobId,
		const FString& DestPackagePath,
		FString& OutError);
}
