// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceLandscapeWeightsLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
	// The one encoding that carries four weight bands in one file. Anything else under
	// material_weights.ue_ready[] is a companion for some other purpose and is not a weight source.
	const TCHAR* const RgbaEncoding = TEXT("png-8bit-rgba");
	constexpr int32 RgbaChannels = 4;
}

int32 FMantlePlaceLandscapeWeightsLogic::SampleIndex(int32 Post, int32 PostCount, int32 PixelCount)
{
	if (PostCount <= 1 || PixelCount <= 0)
	{
		return 0;
	}
	// Posts are samples of a span; pixels are cells covering that same span. u in [0,1] is the
	// position along the span, floor(u*PixelCount) the cell it falls in, and the clamp catches
	// u == 1 landing one past the last cell.
	const double U = static_cast<double>(Post) / static_cast<double>(PostCount - 1);
	return FMath::Clamp(FMath::FloorToInt32(U * PixelCount), 0, PixelCount - 1);
}

bool FMantlePlaceLandscapeWeightsLogic::ResolveBands(
    const FMantlePlaceLandscapeLayer& Layer,
    TArray<FMantlePlaceWeightBand>& OutBands,
    FString& OutError)
{
	OutBands.Reset();

	if (Layer.Materials.Num() == 0)
	{
		OutError = TEXT("landscape_layers.material_weights has no `materials` band legend; without it "
		                "there is no way to know which channel is which material.");
		return false;
	}

	for (const FString& Material : Layer.Materials)
	{
		bool bFound = false;
		for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
		{
			if (Raster.Encoding != RgbaEncoding)
			{
				continue;
			}
			const int32 Channel = Raster.ValueMapping.Bands.IndexOfByKey(Material);
			if (Channel == INDEX_NONE)
			{
				continue;
			}
			if (Channel >= RgbaChannels)
			{
				OutError = FString::Printf(
					TEXT("%s declares material \"%s\" in band %d, but an RGBA texture has only %d channels."),
					*Raster.Path, *Material, Channel, RgbaChannels);
				return false;
			}
			OutBands.Add(FMantlePlaceWeightBand{ Material, Raster.Path, Channel });
			bFound = true;
			break;
		}
		if (!bFound)
		{
			OutError = FString::Printf(
				TEXT("material \"%s\" is in the band legend but no %s companion of "
				     "landscape_layers.material_weights names it."),
				*Material, RgbaEncoding);
			return false;
		}
	}
	return true;
}

bool FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
    const FMantlePlaceLandscapeLayer& Layer,
    const TArray<FMantlePlaceRgbaImage>& Images,
    int32 Size,
    bool bRow0IsNorth,
    TArray<FMantlePlaceWeightPlane>& OutPlanes,
    FString& OutError)
{
	OutPlanes.Reset();

	if (Size <= 0)
	{
		OutError = TEXT("Landscape size must be positive to build weight planes.");
		return false;
	}

	TArray<FMantlePlaceWeightBand> Bands;
	if (!ResolveBands(Layer, Bands, OutError))
	{
		return false;
	}

	for (const FMantlePlaceWeightBand& Band : Bands)
	{
		const FMantlePlaceRgbaImage* Image = Images.FindByPredicate(
			[&Band](const FMantlePlaceRgbaImage& Candidate) { return Candidate.Path == Band.ImagePath; });
		if (Image == nullptr)
		{
			OutError = FString::Printf(TEXT("No decoded image for %s (material \"%s\")."),
				*Band.ImagePath, *Band.Material);
			return false;
		}

		// The manifest states each companion's dimensions; bytes that disagree are not the raster it
		// advertised, so this fails closed rather than sampling whatever did arrive.
		const FMantlePlaceUeReadyRaster* Declared = Layer.UeReady.FindByPredicate(
			[&Band](const FMantlePlaceUeReadyRaster& Candidate) { return Candidate.Path == Band.ImagePath; });
		if (Declared != nullptr && (Declared->Width != Image->Width || Declared->Height != Image->Height))
		{
			OutError = FString::Printf(
				TEXT("%s decoded as %dx%d but the manifest declares %dx%d."),
				*Band.ImagePath, Image->Width, Image->Height, Declared->Width, Declared->Height);
			return false;
		}

		const int64 Expected = static_cast<int64>(Image->Width) * Image->Height * RgbaChannels;
		if (Image->Width <= 0 || Image->Height <= 0 || Image->Pixels.Num() != Expected)
		{
			OutError = FString::Printf(
				TEXT("%s is %dx%d RGBA (%lld bytes expected) but carries %d."),
				*Band.ImagePath, Image->Width, Image->Height, Expected, Image->Pixels.Num());
			return false;
		}

		FMantlePlaceWeightPlane Plane;
		Plane.Material = Band.Material;
		Plane.Data.SetNumUninitialized(Size * Size);
		for (int32 Y = 0; Y < Size; ++Y)
		{
			// Transposed, exactly as the heightmap is in MantlePlaceLandscapeImporter: landscape X is
			// North so it indexes the PNG's ROWS, landscape Y is East so it indexes the COLUMNS. The
			// resample axes swap with it — Y (east) resamples against Image->Width, X against Height.
			const int32 SrcCol = SampleIndex(Y, Size, Image->Width);
			for (int32 X = 0; X < Size; ++X)
			{
				// Landscape X=0 is the south edge; the PNG's row 0 is the north edge when the bundle says so.
				const int32 SampledRow = SampleIndex(X, Size, Image->Height);
				const int32 SrcRow = bRow0IsNorth ? (Image->Height - 1 - SampledRow) : SampledRow;
				const int64 SrcIndex =
					(static_cast<int64>(SrcRow) * Image->Width + SrcCol) * RgbaChannels + Band.Channel;
				Plane.Data[X + Y * Size] = Image->Pixels[SrcIndex];
			}
		}
		OutPlanes.Add(MoveTemp(Plane));
	}
	return true;
}
