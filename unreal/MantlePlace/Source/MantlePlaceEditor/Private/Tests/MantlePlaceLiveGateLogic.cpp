// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLiveGateLogic.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportOrchestration.h"

void FMantlePlaceDzStatistics::Add(double Dz)
{
	++Count;
	Sum += Dz;
	SumSquares += Dz * Dz;
	MaxAbs = FMath::Max(MaxAbs, FMath::Abs(Dz));
}

double FMantlePlaceDzStatistics::Mean() const
{
	return Count > 0 ? Sum / Count : 0.0;
}

double FMantlePlaceDzStatistics::Rms() const
{
	return Count > 0 ? FMath::Sqrt(SumSquares / Count) : 0.0;
}

bool FMantlePlaceLiveGateLogic::TryUnitToMetres(const FString& Units, double& OutMetresPerUnit, FString& OutReason)
{
	if (Units.IsEmpty() || Units.Equals(TEXT("m"), ESearchCase::CaseSensitive))
	{
		OutMetresPerUnit = 1.0;
		return true;
	}
	if (Units.Equals(TEXT("ftUS"), ESearchCase::CaseSensitive))
	{
		OutMetresPerUnit = 1200.0 / 3937.0;
		return true;
	}
	if (Units.Equals(TEXT("ft"), ESearchCase::CaseSensitive))
	{
		OutMetresPerUnit = 0.3048;
		return true;
	}
	OutReason = FString::Printf(
	    TEXT("its surface points are in the unit \"%s\", which this test cannot convert (m, ftUS and ft)"), *Units);
	return false;
}

bool FMantlePlaceLiveGateLogic::MeanWithinTolerance(const FMantlePlaceDzStatistics& Stats)
{
	return Stats.Count > 0 && FMath::Abs(Stats.Mean()) <= MaxMeanOffsetCm;
}

bool FMantlePlaceLiveGateLogic::RmsWithinTolerance(const FMantlePlaceDzStatistics& Stats)
{
	return Stats.Count > 0 && Stats.Rms() <= MaxRmsCm;
}

FString FMantlePlaceLiveGateLogic::DrapeSkipLine(const FMantlePlaceVaultManifest& Manifest)
{
	MantlePlaceArtifacts::FHandlers Drape;
	for (TUniquePtr<IMantlePlaceArtifactHandler>& Handler : MantlePlaceArtifacts::MakeAll())
	{
		if (Handler->Id() == FName(TEXT("drape")))
		{
			Drape.Add(MoveTemp(Handler));
		}
	}
	for (const FString& Line : MantlePlaceImportOrchestration::DescribeFormatSkips(
			 Drape, Manifest, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Both)))
	{
		if (Line.StartsWith(TEXT("Skipped ")))
		{
			return Line;
		}
	}
	return FString();
}

FString FMantlePlaceLiveGateLogic::JudgeDrape(
	const FMantlePlaceVaultManifest& Manifest, const FString& ImportMessage, bool bMaterialIsDrape)
{
	if (Manifest.Drape.bPresent)
	{
		return bMaterialIsDrape
			? FString()
			: FString(TEXT("the import placed an imagery drape, but the landscape component is on the engine default surface"));
	}
	const FString SkipLine = DrapeSkipLine(Manifest);
	if (!SkipLine.IsEmpty())
	{
		return ImportMessage.Contains(SkipLine, ESearchCase::CaseSensitive)
			? FString()
			: FString::Printf(TEXT("the import skipped the imagery drape but its log does not say so; expected: %s"), *SkipLine);
	}
	return ImportMessage.Contains(TEXT("No imagery drape in this bundle (geometry only)."), ESearchCase::CaseSensitive)
		? FString()
		: FString(TEXT("the bundle declares no drape, and the import log does not report the landscape as geometry only"));
}
