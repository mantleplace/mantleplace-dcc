// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceImportManifest.h"

namespace
{
/** `slope` as v19 ships it: a 16-bit ramp in degrees with a nodata sentinel. */
FMantlePlaceRasterValueMapping MakeScaleMapping()
{
	FMantlePlaceRasterValueMapping Mapping;
	Mapping.MinValue = 0.0;
	Mapping.MaxValue = 90.0;
	Mapping.ToValueFormula = TEXT("value = min + (u16/65535)*(max - min)");
	Mapping.Units = TEXT("degrees");
	Mapping.bHasNodata = true;
	Mapping.NodataValue = 0.0;
	return Mapping;
}

/** `worldcover`: ESA class codes that ARE the legend. */
FMantlePlaceRasterValueMapping MakeLegendMapping()
{
	FMantlePlaceRasterValueMapping Mapping;
	Mapping.Classes = { 10, 20, 30, 50, 60, 80 };
	return Mapping;
}

/** `water_mask`. */
FMantlePlaceRasterValueMapping MakeMaskMapping()
{
	FMantlePlaceRasterValueMapping Mapping;
	Mapping.TrueValue = 1.0;
	Mapping.FalseValue = 0.0;
	return Mapping;
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCoverageRasterLogicTest,
    "MantlePlace.Import.CoverageRasterLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCoverageRasterLogicTest::RunTest(const FString& Parameters)
{
	// --- Which layers are coverage rasters ---------------------------------------------------
	{
		TestFalse(TEXT("material_weights is not a coverage raster"),
		    FMantlePlaceCoverageRasterLogic::IsCoverageRaster(TEXT("material_weights")));
		for (const TCHAR* Name : { TEXT("water_mask"), TEXT("worldcover"), TEXT("hillshade"),
		                           TEXT("ndvi"), TEXT("slope"), TEXT("aspect"), TEXT("canopy_height") })
		{
			TestTrue(FString::Printf(TEXT("%s is a coverage raster"), Name),
			    FMantlePlaceCoverageRasterLogic::IsCoverageRaster(Name));
		}
		// A sub-block the ETL adds later defaults IN rather than being silently ignored.
		TestTrue(TEXT("an unknown future layer defaults to coverage raster"),
		    FMantlePlaceCoverageRasterLogic::IsCoverageRaster(TEXT("soil_moisture")));
	}

	// --- Mapping kinds -----------------------------------------------------------------------
	{
		EMantlePlaceCoverageMapping Kind = EMantlePlaceCoverageMapping::Identity;
		FString Error;

		TestTrue(TEXT("a 16-bit ramp is a Scale"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-16bit-grayscale"), MakeScaleMapping(), Kind, Error));
		TestEqual(TEXT("slope is Scale"), Kind, EMantlePlaceCoverageMapping::Scale);

		TestTrue(TEXT("an indexed raster is a Legend"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-8bit-indexed"), MakeLegendMapping(), Kind, Error));
		TestEqual(TEXT("worldcover is Legend"), Kind, EMantlePlaceCoverageMapping::Legend);

		TestTrue(TEXT("a mask raster is a Mask"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-8bit-mask"), MakeMaskMapping(), Kind, Error));
		TestEqual(TEXT("water_mask is Mask"), Kind, EMantlePlaceCoverageMapping::Mask);

		FMantlePlaceRasterValueMapping Identity;
		Identity.bIdentity = true;
		TestTrue(TEXT("an identity 8-bit raster is Identity"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-8bit-grayscale"), Identity, Kind, Error));
		TestEqual(TEXT("hillshade is Identity"), Kind, EMantlePlaceCoverageMapping::Identity);
	}

	// --- Mapping kinds fail closed on a mapping that cannot mean anything --------------------
	{
		EMantlePlaceCoverageMapping Kind = EMantlePlaceCoverageMapping::Identity;
		FString Error;

		// A legend with no classes: the codes would have nothing to resolve against.
		TestFalse(TEXT("an indexed raster with no classes is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-8bit-indexed"), FMantlePlaceRasterValueMapping(), Kind, Error));
		TestTrue(TEXT("and says why"), Error.Contains(TEXT("classes")));

		// A 16-bit ramp with min == max: every sample decodes to one value.
		Error.Reset();
		FMantlePlaceRasterValueMapping Flat = MakeScaleMapping();
		Flat.MaxValue = Flat.MinValue;
		TestFalse(TEXT("a flat 16-bit ramp is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-16bit-grayscale"), Flat, Kind, Error));

		// A mask whose two values are the same masks nothing.
		Error.Reset();
		FMantlePlaceRasterValueMapping DegenerateMask = MakeMaskMapping();
		DegenerateMask.FalseValue = DegenerateMask.TrueValue;
		TestFalse(TEXT("a degenerate mask is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-8bit-mask"), DegenerateMask, Kind, Error));

		Error.Reset();
		TestFalse(TEXT("an unrecognised encoding is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveMappingKind(
		        TEXT("png-32bit-float"), MakeScaleMapping(), Kind, Error));
		TestTrue(TEXT("and names the encoding"), Error.Contains(TEXT("png-32bit-float")));
	}

	// --- Texture settings: the 16-bit channels stay 16-bit ------------------------------------
	{
		FMantlePlaceCoverageTextureSettings Settings;
		FString Error;
		TestTrue(TEXT("a G16 landing resolves"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-16bit-grayscale"), TSF_G16, Settings, Error));

		// TC_Grayscale on a TSF_G16 source is the one combination that reaches PF_G16. This is the
		// assertion that would fail if someone "simplified" it to TC_HalfFloat, whose 10-bit
		// mantissa cannot represent uint16 above 2048.
		TestEqual(TEXT("G16 uses TC_Grayscale"), Settings.Compression, TC_Grayscale);
		TestFalse(TEXT("never sRGB"), Settings.bSRGB);
		TestEqual(TEXT("never mipped"), Settings.MipGen, TMGS_NoMipmaps);
		TestFalse(TEXT("never virtual"), Settings.bVirtualTextureStreaming);
	}

	// --- Texture settings: the 8-bit landings, both of which are legitimate -------------------
	{
		FMantlePlaceCoverageTextureSettings Settings;
		FString Error;

		// Interchange leaves an all-gray RGBA image as BGRA8; the legacy factory collapses it to
		// G8. Same bytes, both valid, different correct compression setting.
		TestTrue(TEXT("an indexed raster landing BGRA8 resolves"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-indexed"), TSF_BGRA8, Settings, Error));
		TestEqual(TEXT("BGRA8 uses the uncompressed path"), Settings.Compression, TC_VectorDisplacementmap);
		TestEqual(TEXT("class codes are point-sampled"), Settings.Filter, TF_Nearest);

		TestTrue(TEXT("the same raster landing G8 also resolves"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-indexed"), TSF_G8, Settings, Error));
		TestEqual(TEXT("G8 uses TC_Grayscale"), Settings.Compression, TC_Grayscale);
		TestFalse(TEXT("G8 is never sRGB — the engine would promote it to BGRA8 and de-gamma it"), Settings.bSRGB);

		TestTrue(TEXT("a mask resolves"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-mask"), TSF_G8, Settings, Error));
		TestEqual(TEXT("a mask is point-sampled too"), Settings.Filter, TF_Nearest);

		TestTrue(TEXT("hillshade resolves"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-grayscale"), TSF_G8, Settings, Error));
		TestEqual(TEXT("continuous shading keeps the default filter"), Settings.Filter, TF_Default);
	}

	// --- Texture settings: a landing that contradicts the encoding is REFUSED -----------------
	//
	// The one failure mode that would otherwise produce a plausible-looking wrong answer instead
	// of a missing asset: a 16-bit channel silently read at 8 bits.
	{
		FMantlePlaceCoverageTextureSettings Settings;
		FString Error;

		TestFalse(TEXT("a 16-bit encoding landing BGRA8 is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-16bit-grayscale"), TSF_BGRA8, Settings, Error));
		TestTrue(TEXT("and names the declared encoding"), Error.Contains(TEXT("png-16bit-grayscale")));
		TestTrue(TEXT("and names what the engine produced"), Error.Contains(TEXT("BGRA8")));

		Error.Reset();
		TestFalse(TEXT("a 16-bit encoding landing G8 is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-16bit-grayscale"), TSF_G8, Settings, Error));

		Error.Reset();
		TestFalse(TEXT("an 8-bit encoding landing G16 is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-indexed"), TSF_G16, Settings, Error));

		Error.Reset();
		TestFalse(TEXT("a float landing is refused"),
		    FMantlePlaceCoverageRasterLogic::ResolveTextureSettings(
		        TEXT("png-8bit-mask"), TSF_RGBA32F, Settings, Error));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
