// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

struct FMantlePlaceDzStatistics
{
	int32 Count = 0;
	double Sum = 0.0;
	double SumSquares = 0.0;
	double MaxAbs = 0.0;

	void Add(double Dz);

	double Mean() const;
	double Rms() const;
};

struct FMantlePlaceLiveGateLogic
{
	static constexpr double MaxMeanOffsetCm = 10.0;

	static constexpr double MaxRmsCm = 100.0;

	static bool TryUnitToMetres(const FString& Units, double& OutMetresPerUnit, FString& OutReason);

	static bool MeanWithinTolerance(const FMantlePlaceDzStatistics& Stats);

	static bool RmsWithinTolerance(const FMantlePlaceDzStatistics& Stats);

	static FString DrapeSkipLine(const FMantlePlaceVaultManifest& Manifest);

	static FString JudgeDrape(const FMantlePlaceVaultManifest& Manifest, const FString& ImportMessage, bool bMaterialIsDrape);
};
