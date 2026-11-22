// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceLandscapeLayer;
struct FMantlePlaceUeReadyRaster;
class UTexture2D;

namespace MantlePlaceCoverageRasters
{
	UTexture2D* ImportRaster(
		const FMantlePlaceLandscapeLayer& Layer,
		const FMantlePlaceUeReadyRaster& Raster,
		const FString& DiskFile,
		const FString& JobId,
		const FString& DestPackagePath,
		FString& OutError);
}
