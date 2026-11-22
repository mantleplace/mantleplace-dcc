// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceRoadSpline
{
	TArray<FVector> PointsUeCm;
	double WidthMEstimated = 0.0;
	FString RoadClass;
	FString Name;
};

struct FMantlePlaceRoadSplinesDatum
{
	FString Stated;

	bool bRequired = false;
};

enum class EMantlePlaceRoadSplinesSource : uint8
{
	EngineCopy,
	SharedLayer,
};

struct FMantlePlaceRoadSplinesCandidate
{
	EMantlePlaceRoadSplinesSource Source = EMantlePlaceRoadSplinesSource::SharedLayer;

	bool bPlaceable = false;

	FString Refusal;
};

struct FMantlePlaceRoadSplinesLogic
{
	static bool IsUtmEpsg(int32 Epsg);

	static bool LonLatToUtm(double LonDeg, double LatDeg, int32 Epsg, double& OutEastingM, double& OutNorthingM);

	static bool CanPlaceHeights(const FMantlePlaceRoadSplinesDatum& Datum, FString& OutReason);

	static bool CanPlaceEngineHeights(const FString& StatedDatum, FString& OutReason);

	static TArray<FMantlePlaceRoadSplinesCandidate> PlanSources(
	    bool bEngineCopyDeclared,
	    const FString& EngineStatedDatum,
	    bool bSharedLayerDeclared,
	    const FMantlePlaceRoadSplinesDatum& SharedDatum);

	static const TCHAR* SourceName(EMantlePlaceRoadSplinesSource Source);

	static FString DescribeSource(EMantlePlaceRoadSplinesSource Source, const FString& Path);

	static bool ParseGeoJson(
	    const FString& JsonText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    FString& OutError);

	static bool AppendLonLatLine(
	    const TArray<FVector>& LonLatZ,
	    const FMantlePlaceRoadSpline& Prototype,
	    double OriginEastingM,
	    double OriginNorthingM,
	    int32 Epsg,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen);

	static FString DescribeOutcome(int32 LinesSeen, int32 SplinesCreated);

	static bool ParseSiteCsv(
	    const FString& CsvText,
	    double SiteHeightM,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen,
	    int32& OutRowsUnreadable,
	    FString& OutError);
};
