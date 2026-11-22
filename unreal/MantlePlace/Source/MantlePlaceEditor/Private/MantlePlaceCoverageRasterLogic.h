// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Engine/TextureDefines.h"
#include "MantlePlaceCoverageRasterTypes.h"

struct FMantlePlaceRasterValueMapping;

struct FMantlePlaceCoverageTextureSettings
{
	TextureCompressionSettings Compression = TC_Grayscale;
	TextureMipGenSettings MipGen = TMGS_NoMipmaps;
	TextureFilter Filter = TF_Default;
	bool bSRGB = false;
	bool bVirtualTextureStreaming = false;
};

struct FMantlePlaceCoverageRasterLogic
{
	static bool IsCoverageRaster(const FString& LayerName);

	static bool ResolveMappingKind(
	    const FString& Encoding,
	    const FMantlePlaceRasterValueMapping& Mapping,
	    EMantlePlaceCoverageMapping& OutKind,
	    FString& OutError);

	static bool ResolveTextureSettings(
	    const FString& Encoding,
	    ETextureSourceFormat LandedFormat,
	    FMantlePlaceCoverageTextureSettings& OutSettings,
	    FString& OutError);
};
