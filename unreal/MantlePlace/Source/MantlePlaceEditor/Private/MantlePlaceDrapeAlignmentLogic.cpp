// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceDrapeAlignmentLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
const TCHAR* const MatchesPrefix = TEXT("matches heightmap");
const TCHAR* const UnverifiedPrefix = TEXT("unverified");
const TCHAR* const DivergesPrefix = TEXT("diverges from heightmap extent");

bool ReadNumberAfter(const FString& Text, const TCHAR* Anchor, double& OutValue)
{
	const int32 AnchorIndex = Text.Find(Anchor, ESearchCase::IgnoreCase, ESearchDir::FromStart);
	if (AnchorIndex == INDEX_NONE)
	{
		return false;
	}

	int32 Index = AnchorIndex + FCString::Strlen(Anchor);
	while (Index < Text.Len() && !FChar::IsDigit(Text[Index]) && Text[Index] != TEXT('-'))
	{
		++Index;
	}
	if (Index >= Text.Len())
	{
		return false;
	}

	const int32 Start = Index;
	if (Text[Index] == TEXT('-'))
	{
		++Index;
	}
	bool bSeenDigit = false;
	bool bSeenDot = false;
	while (Index < Text.Len())
	{
		const TCHAR Char = Text[Index];
		if (FChar::IsDigit(Char))
		{
			bSeenDigit = true;
		}
		else if (Char == TEXT('.') && !bSeenDot)
		{
			bSeenDot = true;
		}
		else
		{
			break;
		}
		++Index;
	}
	if (!bSeenDigit)
	{
		return false;
	}

	OutValue = FCString::Atod(*Text.Mid(Start, Index - Start));
	return true;
}
}

FMantlePlaceDrapeAlignment FMantlePlaceDrapeAlignmentLogic::Classify(const FString& Declared)
{
	FMantlePlaceDrapeAlignment Alignment;
	Alignment.Declared = Declared;

	const FString Trimmed = Declared.TrimStartAndEnd();
	if (Trimmed.IsEmpty())
	{
		Alignment.Shape = EMantlePlaceDrapeAlignment::Absent;
		return Alignment;
	}

	if (Trimmed.StartsWith(MatchesPrefix, ESearchCase::IgnoreCase))
	{
		Alignment.Shape = EMantlePlaceDrapeAlignment::Matches;
		return Alignment;
	}
	if (Trimmed.StartsWith(UnverifiedPrefix, ESearchCase::IgnoreCase))
	{
		Alignment.Shape = EMantlePlaceDrapeAlignment::Unverified;
		return Alignment;
	}
	if (Trimmed.StartsWith(DivergesPrefix, ESearchCase::IgnoreCase))
	{
		Alignment.Shape = EMantlePlaceDrapeAlignment::Diverges;
		double Covered = 0.0;
		double Overshoot = 0.0;
		if (ReadNumberAfter(Trimmed, TEXT("covers"), Covered)
			&& ReadNumberAfter(Trimmed, TEXT("overshoot"), Overshoot))
		{
			Alignment.bHasMeasured = true;
			Alignment.CoveredPercent = Covered;
			Alignment.OvershootRatio = Overshoot;
		}
		return Alignment;
	}

	Alignment.Shape = EMantlePlaceDrapeAlignment::Unrecognised;
	return Alignment;
}

bool FMantlePlaceDrapeAlignmentLogic::CheckAgainstExtents(
    const FMantlePlaceVaultManifest& Manifest,
    const FMantlePlaceDrapeAlignment& Alignment,
    FString& OutError)
{
	if (Alignment.Shape != EMantlePlaceDrapeAlignment::Matches)
	{
		return true;
	}
	if (!Manifest.Drape.bPresent || !Manifest.Heightmap.bPresent)
	{
		return true;
	}

	const FVector2D AoiSize = Manifest.GetAoiSizeUeCm();
	if (AoiSize.X <= 0.0 || AoiSize.Y <= 0.0)
	{
		return true;
	}

	FVector2D DrapeMin, DrapeSize;
	Manifest.GetDrapeWorldRect(DrapeMin, DrapeSize);

	const double NorthRatio = DrapeSize.X / AoiSize.X;
	const double EastRatio = DrapeSize.Y / AoiSize.Y;
	if (FMath::Abs(NorthRatio - 1.0) <= ExtentSlack && FMath::Abs(EastRatio - 1.0) <= ExtentSlack)
	{
		return true;
	}

	OutError = FString::Printf(
	    TEXT("manifest hosts.unreal.imagery_drape declares \"%s\", but its own extent spans "
	         "%.1f x %.1f m against a heightmap AOI of %.1f x %.1f m (%.1f%% x %.1f%%). The bundle "
	         "contradicts itself, and importing it would drape the imagery wrong without saying so."),
	    *Alignment.Declared,
	    DrapeSize.X / 100.0, DrapeSize.Y / 100.0,
	    AoiSize.X / 100.0, AoiSize.Y / 100.0,
	    100.0 * NorthRatio, 100.0 * EastRatio);
	return false;
}

FString FMantlePlaceDrapeAlignmentLogic::DescribeForLog(const FMantlePlaceDrapeAlignment& Alignment)
{
	switch (Alignment.Shape)
	{
	case EMantlePlaceDrapeAlignment::Absent:
		return FString();

	case EMantlePlaceDrapeAlignment::Diverges:
		return Alignment.bHasMeasured
			? FString::Printf(
				TEXT("WARNING: the bundle reports its imagery diverges from the heightmap extent — "
				     "imagery covers %g%% of the heightmap, overshoot %gx. The drape is placed on "
				     "its declared extent, so the import proceeds; expect imagery that does not "
				     "blanket the terrain. Manifest: \"%s\"."),
				Alignment.CoveredPercent, Alignment.OvershootRatio, *Alignment.Declared)
			: FString::Printf(
				TEXT("WARNING: the bundle reports its imagery diverges from the heightmap extent. "
				     "The drape is placed on its declared extent, so the import proceeds; expect "
				     "imagery that does not blanket the terrain. Manifest: \"%s\"."),
				*Alignment.Declared);

	case EMantlePlaceDrapeAlignment::Unrecognised:
		return FString::Printf(
			TEXT("Imagery drape alignment (a descriptor this plugin version does not recognise, "
			     "reported unchanged): \"%s\"."),
			*Alignment.Declared);

	default:
		return FString::Printf(TEXT("Imagery drape alignment: \"%s\"."), *Alignment.Declared);
	}
}
