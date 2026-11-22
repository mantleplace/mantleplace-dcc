// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

enum class EMantlePlaceDrapeAlignment : uint8
{
	Absent,
	Matches,
	Unverified,
	Diverges,
	Unrecognised,
};

struct FMantlePlaceDrapeAlignment
{
	EMantlePlaceDrapeAlignment Shape = EMantlePlaceDrapeAlignment::Absent;

	FString Declared;

	bool bHasMeasured = false;

	double CoveredPercent = 0.0;

	double OvershootRatio = 0.0;

	bool ShouldWarn() const { return Shape == EMantlePlaceDrapeAlignment::Diverges; }
};

struct FMantlePlaceDrapeAlignmentLogic
{
	static constexpr double ExtentSlack = 0.01;

	static FMantlePlaceDrapeAlignment Classify(const FString& Declared);

	static bool CheckAgainstExtents(
	    const FMantlePlaceVaultManifest& Manifest,
	    const FMantlePlaceDrapeAlignment& Alignment,
	    FString& OutError);

	static FString DescribeForLog(const FMantlePlaceDrapeAlignment& Alignment);
};
