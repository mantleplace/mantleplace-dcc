// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceFrameOrigin
{
	FString Crs;
	FString VerticalDatum;
	double EastingM = 0.0;
	double NorthingM = 0.0;
	bool bStated = false;
};

struct FMantlePlaceFrameRecord
{
	bool bPlaced = false;
	FMantlePlaceFrameOrigin Origin;
	double OffsetEastM = 0.0;
	double OffsetNorthM = 0.0;
};

struct FMantlePlaceLevelImportTag
{
	FString Identity;
	FString FrameValue;
};

enum class EMantlePlaceFrameOutcome : uint8
{
	Anchor,
	Offset,
	Centred,
};

enum class EMantlePlaceFrameRefusal : uint8
{
	None,
	OriginUnstated,
	CrsDiffers,
	DatumDiffers,
	TooFar,
};

struct FMantlePlaceFramePlan
{
	EMantlePlaceFrameOutcome Outcome = EMantlePlaceFrameOutcome::Anchor;
	EMantlePlaceFrameRefusal Refusal = EMantlePlaceFrameRefusal::None;

	double OffsetEastM = 0.0;
	double OffsetNorthM = 0.0;

	FString Warning;
	FString Note;
	FString FrameValue;

	bool HasOffset() const { return Outcome == EMantlePlaceFrameOutcome::Offset && (OffsetEastM != 0.0 || OffsetNorthM != 0.0); }
};

class FMantlePlaceLevelFrameLogic
{
public:
	static constexpr double MaxOffsetM = 100000.0;

	static FString EncodeFrame(const FMantlePlaceFrameRecord& Record);

	static bool DecodeFrame(const FString& Value, FMantlePlaceFrameRecord& OutRecord);

	static FMantlePlaceFramePlan Plan(
		const FString& ThisIdentity, const FMantlePlaceFrameOrigin& ThisOrigin,
		const TArray<FMantlePlaceLevelImportTag>& InLevel);
};
