// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceManifestBlocks.h"

struct FMantlePlaceRecordPolyline
{
	TArray<FVector2D> Points;
	bool bClosed = false;
};

struct FMantlePlacePublicRecordsLogic
{
	static bool IsKnownFamily(const FString& Family);

	static FString DisplayName(const FString& Family);

	static void OrderLayers(TArray<FMantlePlaceRecordLayer>& Layers);

	static FMantlePlaceRecordFrame StatedFrame(
	    const FString& Type, const FString& Crs, bool bHasUnitFactor, double UnitToMetres, const FString& UnitName = FString());

	static FString FrameTypeOfCoordinateMode(const FString& CoordinateMode);

	static bool CanPlaceFrame(const FMantlePlaceRecordFrame& Frame, const FString& HostCrs, FString& OutReason);

	static bool ParseDxf(
	    const FString& Text,
	    TArray<FMantlePlaceRecordPolyline>& OutLines,
	    TArray<FVector2D>& OutPoints,
	    int32& OutEntitiesSkipped,
	    FString& OutError);

	static constexpr double MarkerRadiusM = 4.0;

	static FMantlePlaceRecordPolyline MarkerOutline(const FVector2D& Point);

	static bool AnyVertexOnSite(
	    const TArray<FMantlePlaceRecordPolyline>& Lines,
	    double OriginEastingM,
	    double OriginNorthingM,
	    const FVector2D& SpanUeCm);

	static FString StandingNotice();

	static TArray<FString> DescribeLayer(const FMantlePlaceRecordLayer& Layer);

	static FString DescribePointer(const FString& Family, const FMantlePlaceRecordPointer& Pointer);

	static FString DescribeSkippedSource(const FMantlePlaceSkippedSource& Source);

	static TArray<FString> DescribeNotDelivered(const FMantlePlacePublicRecordsBlock& Block);
};
