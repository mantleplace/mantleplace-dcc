// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct MANTLEPLACERUNTIME_API FMantlePlaceExtent
{
	double West = 0.0;
	double South = 0.0;
	double East = 0.0;
	double North = 0.0;

	bool IsValid() const;
};

struct FMantlePlaceGeoPoint
{
	double Longitude = 0.0;
	double Latitude = 0.0;
};

struct FMantlePlaceExtentBoxState
{
	double CenterLongitude = 0.0;
	double CenterLatitude = 0.0;
	double WidthM = 0.0;
	double DepthM = 0.0;
};

enum class EMantlePlaceExtentSource : uint8
{
	None,
	PriorImport,
	NativeGeoreference,
	CesiumGeoreference,
	ExtentBox,
};

struct FMantlePlaceLevelState
{
	TArray<FMantlePlaceExtent> ImportExtents;

	TArray<FMantlePlaceGeoPoint> NativeGeoreferencedPoints;

	TArray<FMantlePlaceGeoPoint> CesiumGeoreferencedPoints;

	TArray<FMantlePlaceExtentBoxState> ExtentBoxes;
};

struct FMantlePlaceResolvedExtent
{
	EMantlePlaceExtentSource Source = EMantlePlaceExtentSource::None;
	FMantlePlaceExtent Extent;

	bool IsSet() const { return Source != EMantlePlaceExtentSource::None; }
};

struct MANTLEPLACERUNTIME_API FMantlePlaceCuratorExtentLogic
{
	static const TCHAR* From();

	static bool FromPriorImports(const TArray<FMantlePlaceExtent>& ImportExtents, FMantlePlaceExtent& OutExtent);

	static bool FromNativeGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent);
	static bool FromCesiumGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent);

	static bool FromExtentBoxes(const TArray<FMantlePlaceExtentBoxState>& Boxes, FMantlePlaceExtent& OutExtent);

	static bool FromExtentBox(const FMantlePlaceExtentBoxState& Box, FMantlePlaceExtent& OutExtent);

	static FMantlePlaceResolvedExtent Resolve(const FMantlePlaceLevelState& Level);

	static FString DescribeSource(EMantlePlaceExtentSource Source);

	static FString BuildCuratorUrl(const FString& BaseUrl, const FMantlePlaceResolvedExtent& Resolved);

	static FString FormatBound(double Value, bool bRoundUp);

	static FString FormatToken(const FMantlePlaceExtent& Extent);
};
