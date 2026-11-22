// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceLandscapeLayer;

struct FMantlePlaceRgbaImage
{
	FString Path;
	int32 Width = 0;
	int32 Height = 0;
	TArray<uint8> Pixels;
};

struct FMantlePlaceWeightBand
{
	FString Material;
	FString ImagePath;
	int32 Channel = 0;
};

struct FMantlePlaceWeightPlane
{
	FString Material;
	TArray<uint8> Data;
};

struct FMantlePlaceLandscapeWeightsLogic
{
	static const TCHAR* const GrayscaleEncoding;

	static bool ResolveBands(
	    const FMantlePlaceLandscapeLayer& Layer,
	    TArray<FMantlePlaceWeightBand>& OutBands,
	    FString& OutError);

	static bool BuildWeightPlanes(
	    const FMantlePlaceLandscapeLayer& Layer,
	    const TArray<FMantlePlaceRgbaImage>& Images,
	    int32 Size,
	    bool bRow0IsNorth,
	    TArray<FMantlePlaceWeightPlane>& OutPlanes,
	    FString& OutError);

	static int32 SampleIndex(int32 Post, int32 PostCount, int32 PixelCount);
};
