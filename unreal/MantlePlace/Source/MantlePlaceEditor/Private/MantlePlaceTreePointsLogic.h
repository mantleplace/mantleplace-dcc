// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceLandcoverTypes.h"
#include "MantlePlaceManifestBlocks.h"

enum class EMantlePlaceTreePointsOutcome : uint8
{
	Parsed,

	HeaderUnrecognised,

	CountMismatch,

	Unplaceable,
};

struct FMantlePlaceTreePointsFrame
{
	FString Crs;
	FString Units;
	FString HorizontalUnits;

	FString HostCrs;

	bool bRequired = false;

	bool IsOwed() const
	{
		return bRequired || !Crs.IsEmpty() || !Units.IsEmpty() || !HorizontalUnits.IsEmpty();
	}
};

struct FMantlePlaceResolvedTreeLevel
{
	EMantlePlaceTreeLevel Applied = EMantlePlaceTreeLevel::Max;
	FString Path;
	FString Sha256;
	int32 DeclaredCount = 0;
	int32 RowLimit = 0;
	bool bOwnFrame = false;
	FString Crs;
	FString Units;
	FString HorizontalUnits;
	FString FallbackReason;
};

struct FMantlePlaceTreePointsLogic
{
	static EMantlePlaceTreePointsOutcome ParseCsv(
	    const FString& CsvText,
	    double OriginEastingM,
	    double OriginNorthingM,
	    const FMantlePlaceTreePointsFrame& Frame,
	    const FVector2D& LandscapeSpanUeCm,
	    int32 DeclaredPointCount,
	    TArray<FMantlePlaceTreePointRow>& OutRows,
	    FString& OutError,
	    const FString& FoliageTypeVocabulary = FString(),
	    FString* OutNote = nullptr);

	static constexpr double EdgeToleranceCmValue = 10.0;

	static constexpr int32 MaxOutsideSharePercentValue = 5;

	static EMantlePlaceFoliageType ReadFoliageType(const FString& Cell);

	static FMantlePlaceResolvedTreeLevel ResolveLevel(
	    const FMantlePlaceFoliagePointsBlock& Block, EMantlePlaceTreeLevel Requested);

	static FString CutRows(const FString& CsvText, int32 Rows);

	static const TArray<FName>& ColumnContract();
};
