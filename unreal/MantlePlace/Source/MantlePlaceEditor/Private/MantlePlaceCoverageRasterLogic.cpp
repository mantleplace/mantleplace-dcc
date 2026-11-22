// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceCoverageRasterLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
	const TCHAR* const EncodingGray16 = TEXT("png-16bit-grayscale");
	const TCHAR* const EncodingGray8 = TEXT("png-8bit-grayscale");
	const TCHAR* const EncodingMask8 = TEXT("png-8bit-mask");
	const TCHAR* const EncodingIndexed8 = TEXT("png-8bit-indexed");

	FString DescribeSourceFormat(ETextureSourceFormat Format)
	{
		switch (Format)
		{
		case TSF_G8:      return TEXT("G8");
		case TSF_G16:     return TEXT("G16");
		case TSF_BGRA8:   return TEXT("BGRA8");
		case TSF_BGRE8:   return TEXT("BGRE8");
		case TSF_RGBA16:  return TEXT("RGBA16");
		case TSF_RGBA16F: return TEXT("RGBA16F");
		case TSF_RGBA32F: return TEXT("RGBA32F");
		case TSF_R16F:    return TEXT("R16F");
		case TSF_R32F:    return TEXT("R32F");
		default:          return FString::Printf(TEXT("source format %d"), static_cast<int32>(Format));
		}
	}
}

bool FMantlePlaceCoverageRasterLogic::IsCoverageRaster(const FString& LayerName)
{
	return !LayerName.Equals(TEXT("material_weights"), ESearchCase::IgnoreCase);
}

bool FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
    const FString& Encoding,
    const FMantlePlaceRasterValueMapping& Mapping,
    EMantlePlaceCoverageMapping& OutKind,
    FString& OutError)
{
	if (Encoding.Equals(EncodingIndexed8, ESearchCase::IgnoreCase))
	{
		if (Mapping.Classes.Num() == 0)
		{
			OutError = TEXT("An indexed raster declares no classes[], so its codes have no legend.");
			return false;
		}
		OutKind = EMantlePlaceCoverageMapping::Legend;
		return true;
	}

	if (Encoding.Equals(EncodingMask8, ESearchCase::IgnoreCase))
	{
		if (Mapping.TrueValue == Mapping.FalseValue)
		{
			OutError = TEXT("A mask raster declares the same true_value and false_value, so it masks nothing.");
			return false;
		}
		OutKind = EMantlePlaceCoverageMapping::Mask;
		return true;
	}

	if (Encoding.Equals(EncodingGray16, ESearchCase::IgnoreCase))
	{
		if (Mapping.MinValue == Mapping.MaxValue)
		{
			OutError = TEXT("A 16-bit raster declares min == max, so its samples decode to a single value.");
			return false;
		}
		OutKind = EMantlePlaceCoverageMapping::Scale;
		return true;
	}

	if (Encoding.Equals(EncodingGray8, ESearchCase::IgnoreCase))
	{
		OutKind = Mapping.bIdentity || Mapping.MinValue == Mapping.MaxValue
		              ? EMantlePlaceCoverageMapping::Identity
		              : EMantlePlaceCoverageMapping::Scale;
		return true;
	}

	OutError = FString::Printf(TEXT("Unrecognised coverage-raster encoding \"%s\"."), *Encoding);
	return false;
}

bool FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
    const FString& Encoding,
    ETextureSourceFormat LandedFormat,
    FMantlePlaceCoverageTextureSettings& OutSettings,
    FString& OutError)
{
	const bool bIndexed = Encoding.Equals(EncodingIndexed8, ESearchCase::IgnoreCase);
	const bool bMask = Encoding.Equals(EncodingMask8, ESearchCase::IgnoreCase);
	const bool bGray16 = Encoding.Equals(EncodingGray16, ESearchCase::IgnoreCase);
	const bool bGray8 = Encoding.Equals(EncodingGray8, ESearchCase::IgnoreCase);

	if (!bIndexed && !bMask && !bGray16 && !bGray8)
	{
		OutError = FString::Printf(TEXT("Unrecognised coverage-raster encoding \"%s\"."), *Encoding);
		return false;
	}

	FMantlePlaceCoverageTextureSettings Settings;

	Settings.bSRGB = false;

	Settings.MipGen = TMGS_NoMipmaps;

	Settings.bVirtualTextureStreaming = false;

	Settings.Filter = (bIndexed || bMask) ? TF_Nearest : TF_Default;

	if (bGray16)
	{
		if (LandedFormat != TSF_G16)
		{
			OutError = FString::Printf(
			    TEXT("The manifest declares %s but the engine imported this raster as %s; a 16-bit "
			         "channel read at that depth would be silently wrong rather than missing."),
			    EncodingGray16, *DescribeSourceFormat(LandedFormat));
			return false;
		}
		Settings.Compression = TC_Grayscale;
		OutSettings = Settings;
		return true;
	}

	switch (LandedFormat)
	{
	case TSF_G8:
		Settings.Compression = TC_Grayscale;
		break;

	case TSF_BGRA8:
		Settings.Compression = TC_VectorDisplacementmap;
		break;

	case TSF_G16:
		OutError = FString::Printf(
		    TEXT("The manifest declares %s but the engine imported this raster as G16; the declared "
		         "encoding and the actual bit depth disagree."),
		    *Encoding);
		return false;

	default:
		OutError = FString::Printf(
		    TEXT("The manifest declares %s but the engine imported this raster as %s, which is "
		         "neither of the 8-bit landings that encoding can produce."),
		    *Encoding, *DescribeSourceFormat(LandedFormat));
		return false;
	}

	OutSettings = Settings;
	return true;
}
