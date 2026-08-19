// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceCoverageRasterLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
	/** Encoding tokens, spelled once. These are contract values (HPS-32) — matched, never parsed. */
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
	// Defined by exclusion on purpose. A new sub-block the ETL adds later is a coverage raster by
	// default, which is the safe direction: it gets imported as data with its mapping attached
	// rather than silently ignored, and only a layer that changes what the Landscape IS has to be
	// named here.
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
		// A 16-bit raster is a ramp between two numbers; without them there is no way back from a
		// 0-65535 sample to metres or degrees, which is exactly the state this whole payload exists
		// to prevent. `identity` is not a legal escape here — an identity 16-bit raster would mean
		// the sample IS the value, and the contract only publishes identity for the 8-bit case.
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
		// hillshade: already-baked 8-bit shading, copied through verbatim. A scale is tolerated
		// here because an 8-bit grayscale layer MAY publish one instead of `identity: true`.
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

	// Never sRGB. The importer force-enables it for 16-bit sources ("counter-intuitively, U16 and
	// F32 always want SRGB on") because it assumes colour; on a data raster it puts a gamma curve
	// between the sample and its value and picks a Grayscale rather than LinearGrayscale sampler.
	Settings.bSRGB = false;

	// Never mips. Every mip level averages neighbouring samples, which invents class codes that are
	// not in the legend and blends real values with the nodata sentinel.
	Settings.MipGen = TMGS_NoMipmaps;

	// Never virtual. AutoVTSize (4096) would VT the imagery-grid `ndvi` raster and nothing else,
	// so exactly one of a bundle's seven coverage rasters would need a different sampler node in a
	// material — an inconsistency a consumer has no reason to suspect. Seven textures from one
	// importer with one documented meaning sample identically.
	Settings.bVirtualTextureStreaming = false;

	// Categorical data is point-sampled: interpolating between two class codes, or between "water"
	// and "not water", produces a value the source never claimed. Continuous channels are left at
	// the default filter — a consumer that wants exact texel reads can still ask for them, and the
	// nodata sentinel it must exclude is carried on the payload.
	Settings.Filter = (bIndexed || bMask) ? TF_Nearest : TF_Default;

	if (bGray16)
	{
		// TSF_G16 + TC_Grayscale is the one combination that reaches PF_G16 and keeps all 16 bits.
		// Anything else means the PNG was not the 16-bit grayscale the contract declared — most
		// likely a tRNS chunk forcing an RGBA decode — and TC_Grayscale on that silently yields G8.
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

	// The three 8-bit encodings. Which single-vs-multi-channel format they land in is path-
	// dependent rather than declared: the legacy factory collapses an all-gray RGBA image to G8
	// while Interchange leaves it BGRA8, so both are legitimate landings for the same bytes and the
	// compression setting follows the landing.
	switch (LandedFormat)
	{
	case TSF_G8:
		// PF_G8, uncompressed. SRGB must stay false — the engine promotes an sRGB G8 to BGRA8 and
		// then de-gammas the sample in the shader.
		Settings.Compression = TC_Grayscale;
		break;

	case TSF_BGRA8:
		// TC_VectorDisplacementmap is the uncompressed BGRA8 path: no block compression to smear
		// the codes, and no sRGB by default. The code is recovered as round(Sample.R * 255).
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
