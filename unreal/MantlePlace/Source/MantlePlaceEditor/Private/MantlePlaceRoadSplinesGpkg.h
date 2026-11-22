// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

#include "MantlePlaceRoadSplinesLogic.h"

struct FMantlePlaceRoadSplinesGpkg
{
	static constexpr int32 Wgs84SrsId = 4326;

	static bool ReadBytes(
	    const TArray<uint8>& GpkgBytes,
	    const TArray<FString>& Tables,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    int32& OutRowsUnreadable,
	    FString& OutError);

	static bool ReadFile(
	    const FString& GpkgFilePath,
	    const TArray<FString>& Tables,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    int32& OutRowsUnreadable,
	    FString& OutError);

	static bool DecodeGeometry(
	    const TArray<uint8>& Blob,
	    TArray<TArray<FVector>>& OutParts,
	    bool& OutIsLine,
	    FString& OutError);
};
