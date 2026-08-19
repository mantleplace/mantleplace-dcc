// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLandscapeWeightsLogic.h"

namespace
{
/** The material_weights layer as v19 ships it: eight materials across two 4-band RGBA companions. */
FMantlePlaceLandscapeLayer MakeLayer(int32 Width, int32 Height)
{
	FMantlePlaceUeReadyRaster First;
	First.Path = TEXT("Landcover/MaterialWeights_1_4.png");
	First.Encoding = TEXT("png-8bit-rgba");
	First.Width = Width;
	First.Height = Height;
	First.ValueMapping.Bands = { TEXT("water"), TEXT("grass"), TEXT("forest"), TEXT("dirt") };

	FMantlePlaceUeReadyRaster Second = First;
	Second.Path = TEXT("Landcover/MaterialWeights_5_8.png");
	Second.ValueMapping.Bands = { TEXT("rock"), TEXT("sand"), TEXT("snow"), TEXT("built") };

	FMantlePlaceLandscapeLayer Layer;
	Layer.Name = TEXT("material_weights");
	Layer.Path = TEXT("Landcover/MaterialWeights.tif");
	Layer.Materials = { TEXT("water"), TEXT("grass"), TEXT("forest"), TEXT("dirt"),
	                    TEXT("rock"), TEXT("sand"), TEXT("snow"), TEXT("built") };
	Layer.UeReady = { First, Second };
	return Layer;
}

/** An RGBA image whose every channel encodes its own pixel position, so a mis-sample is visible. */
FMantlePlaceRgbaImage MakeImage(const FString& Path, int32 Width, int32 Height, uint8 ChannelBase)
{
	FMantlePlaceRgbaImage Image;
	Image.Path = Path;
	Image.Width = Width;
	Image.Height = Height;
	Image.Pixels.SetNumUninitialized(Width * Height * 4);
	for (int32 Row = 0; Row < Height; ++Row)
	{
		for (int32 Col = 0; Col < Width; ++Col)
		{
			for (int32 Channel = 0; Channel < 4; ++Channel)
			{
				Image.Pixels[(Row * Width + Col) * 4 + Channel] =
					static_cast<uint8>(ChannelBase + Channel * 40 + Row * 4 + Col);
			}
		}
	}
	return Image;
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceLandscapeWeightsLogicTest,
    "MantlePlace.Import.LandscapeWeightsLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLandscapeWeightsLogicTest::RunTest(const FString& Parameters)
{
	// --- The band legend resolves to (image, channel) in legend order ------------------------
	{
		const FMantlePlaceLandscapeLayer Layer = MakeLayer(2, 2);
		TArray<FMantlePlaceWeightBand> Bands;
		FString Error;
		TestTrue(TEXT("legend resolves"), FMantlePlaceLandscapeWeightsLogic::ResolveBands(Layer, Bands, Error));
		TestEqual(TEXT("eight bands"), Bands.Num(), 8);
		if (Bands.Num() == 8)
		{
			TestEqual(TEXT("water is band 0 of the first half"), Bands[0].Channel, 0);
			TestEqual(TEXT("water reads the 1_4 half"), Bands[0].ImagePath, FString(TEXT("Landcover/MaterialWeights_1_4.png")));
			TestEqual(TEXT("dirt is band 3 of the first half"), Bands[3].Channel, 3);
			TestEqual(TEXT("rock is band 0 of the SECOND half"), Bands[4].Channel, 0);
			TestEqual(TEXT("rock reads the 5_8 half"), Bands[4].ImagePath, FString(TEXT("Landcover/MaterialWeights_5_8.png")));
			TestEqual(TEXT("built is band 3 of the second half"), Bands[7].Channel, 3);
			TestEqual(TEXT("legend order is preserved"), Bands[6].Material, FString(TEXT("snow")));
		}
	}

	// --- Fail closed: no legend, and a material no companion carries --------------------------
	{
		FMantlePlaceLandscapeLayer NoLegend = MakeLayer(2, 2);
		NoLegend.Materials.Reset();
		TArray<FMantlePlaceWeightBand> Bands;
		FString Error;
		TestFalse(TEXT("a layer with no band legend is refused"),
			FMantlePlaceLandscapeWeightsLogic::ResolveBands(NoLegend, Bands, Error));
		TestFalse(TEXT("and says why"), Error.IsEmpty());

		FMantlePlaceLandscapeLayer Unbacked = MakeLayer(2, 2);
		Unbacked.Materials.Add(TEXT("lava"));
		Error.Reset();
		TestFalse(TEXT("a material no companion names is refused"),
			FMantlePlaceLandscapeWeightsLogic::ResolveBands(Unbacked, Bands, Error));
		TestTrue(TEXT("and names the material"), Error.Contains(TEXT("lava")));

		// A companion in some other encoding is not a weight source, so the legend cannot resolve.
		FMantlePlaceLandscapeLayer NotRgba = MakeLayer(2, 2);
		for (FMantlePlaceUeReadyRaster& Raster : NotRgba.UeReady)
		{
			Raster.Encoding = TEXT("png-16bit-grayscale");
		}
		Error.Reset();
		TestFalse(TEXT("a non-RGBA companion is not a weight source"),
			FMantlePlaceLandscapeWeightsLogic::ResolveBands(NotRgba, Bands, Error));
	}

	// --- SampleIndex maps corner to corner, both up- and down-sampling ------------------------
	{
		// 3 posts over 2 pixels: first post -> first pixel, last post -> last pixel.
		TestEqual(TEXT("first post"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(0, 3, 2), 0);
		TestEqual(TEXT("middle post"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(1, 3, 2), 1);
		TestEqual(TEXT("last post never runs off the end"),
			FMantlePlaceLandscapeWeightsLogic::SampleIndex(2, 3, 2), 1);

		// Down-sampling: 3 posts over 143 pixels (the real bundle's weight raster width).
		TestEqual(TEXT("first of 143"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(0, 3, 143), 0);
		TestEqual(TEXT("mid of 143"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(1, 3, 143), 71);
		TestEqual(TEXT("last of 143"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(2, 3, 143), 142);

		// A single post has nowhere to interpolate to; a zero-width raster has nothing to read.
		TestEqual(TEXT("one post"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(0, 1, 143), 0);
		TestEqual(TEXT("no pixels"), FMantlePlaceLandscapeWeightsLogic::SampleIndex(2, 3, 0), 0);
	}

	// --- The real shape: a 2x2 raster resampled onto a 3x3 Landscape, rows flipped ------------
	{
		const FMantlePlaceLandscapeLayer Layer = MakeLayer(2, 2);
		const TArray<FMantlePlaceRgbaImage> Images = {
			MakeImage(TEXT("Landcover/MaterialWeights_1_4.png"), 2, 2, 0),
			MakeImage(TEXT("Landcover/MaterialWeights_5_8.png"), 2, 2, 100),
		};

		TArray<FMantlePlaceWeightPlane> Planes;
		FString Error;
		TestTrue(TEXT("planes build"), FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
		                                   Layer, Images, /*Size*/ 3, /*bRow0IsNorth*/ true, Planes, Error));
		TestEqual(TEXT("no error"), Error, FString());
		TestEqual(TEXT("one plane per material"), Planes.Num(), 8);

		if (Planes.Num() == 8)
		{
			TestEqual(TEXT("plane order follows the legend"), Planes[0].Material, FString(TEXT("water")));
			TestEqual(TEXT("plane is Size*Size"), Planes[0].Data.Num(), 9);

			// water == channel 0 of the 1_4 half, whose pixel (Row,Col) value is Row*4 + Col.
			// Landscape row 0 is SOUTH, so it reads the PNG's LAST row (Row 1): values 4 and 5.
			const TArray<uint8>& Water = Planes[0].Data;
			TestEqual(TEXT("south-west post reads PNG row 1, col 0"), static_cast<int32>(Water[0]), 4);
			TestEqual(TEXT("south-east post reads PNG row 1, col 1"), static_cast<int32>(Water[2]), 5);
			// Landscape row 2 is NORTH, so it reads the PNG's row 0: values 0 and 1.
			TestEqual(TEXT("north-west post reads PNG row 0, col 0"), static_cast<int32>(Water[6]), 0);
			TestEqual(TEXT("north-east post reads PNG row 0, col 1"), static_cast<int32>(Water[8]), 1);

			// built == channel 3 of the 5_8 half: base 100 + 3*40 + Row*4 + Col.
			const TArray<uint8>& Built = Planes[7].Data;
			TestEqual(TEXT("built reads the second half, channel 3"), static_cast<int32>(Built[0]), 224);
			TestEqual(TEXT("built north-east"), static_cast<int32>(Built[8]), 221);
		}

		// row0_is_north false means no flip: the south row reads the PNG's first row.
		TArray<FMantlePlaceWeightPlane> Unflipped;
		Error.Reset();
		TestTrue(TEXT("planes build unflipped"), FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
		                                             Layer, Images, 3, /*bRow0IsNorth*/ false, Unflipped, Error));
		if (Unflipped.Num() == 8)
		{
			TestEqual(TEXT("no flip: south post reads PNG row 0"), static_cast<int32>(Unflipped[0].Data[0]), 0);
		}
	}

	// --- Fail closed on bytes that are not what the manifest advertised ----------------------
	{
		const FMantlePlaceLandscapeLayer Layer = MakeLayer(2, 2);
		TArray<FMantlePlaceWeightPlane> Planes;
		FString Error;

		TestFalse(TEXT("a missing companion is refused"),
			FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
				Layer, { MakeImage(TEXT("Landcover/MaterialWeights_1_4.png"), 2, 2, 0) }, 3, true, Planes, Error));
		TestTrue(TEXT("and names the file"), Error.Contains(TEXT("MaterialWeights_5_8.png")));

		Error.Reset();
		const TArray<FMantlePlaceRgbaImage> WrongSize = {
			MakeImage(TEXT("Landcover/MaterialWeights_1_4.png"), 4, 4, 0),
			MakeImage(TEXT("Landcover/MaterialWeights_5_8.png"), 2, 2, 100),
		};
		TestFalse(TEXT("decoded dimensions that contradict the manifest are refused"),
			FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(Layer, WrongSize, 3, true, Planes, Error));

		Error.Reset();
		TArray<FMantlePlaceRgbaImage> Truncated = {
			MakeImage(TEXT("Landcover/MaterialWeights_1_4.png"), 2, 2, 0),
			MakeImage(TEXT("Landcover/MaterialWeights_5_8.png"), 2, 2, 100),
		};
		Truncated[0].Pixels.SetNum(4); // one pixel's worth of a four-pixel image
		TestFalse(TEXT("a truncated companion is refused"),
			FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(Layer, Truncated, 3, true, Planes, Error));

		Error.Reset();
		TestFalse(TEXT("a zero-post landscape is refused"),
			FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(Layer, WrongSize, 0, true, Planes, Error));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
