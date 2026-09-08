// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceDrapeAlignmentLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
/** The three shapes the published schema documents, matched by PREFIX exactly as it instructs.
 *  The unverified shape's own text carries an em dash the ETL may re-punctuate at any time, so the
 *  prefix stops at the first word — matching further would make a typographic edit a behaviour
 *  change. */
const TCHAR* const MatchesPrefix = TEXT("matches heightmap");
const TCHAR* const UnverifiedPrefix = TEXT("unverified");
const TCHAR* const DivergesPrefix = TEXT("diverges from heightmap extent");

/**
 * The first number appearing after `Anchor`, or false when either the anchor or a number after it
 * is absent.
 *
 * Anchored rather than positional because the divergent shape is a sentence, not a format string:
 * "imagery covers 62.4% of the heightmap, overshoot 1.03x". Reading "the first number" and "the
 * second number" would silently swap the two the day the ETL rewords the sentence, whereas an
 * anchor that has moved reads as absent and downgrades to the verbatim string.
 */
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
} // namespace

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
		// Both or neither: a warning that quotes one of the two measurements and invents the other
		// is worse than one that quotes the manifest's sentence verbatim, which is the fallback.
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

	// A fourth shape from a later minor. Additive is only additive if consumers tolerate it
	// (spec/compatibility.md), and this descriptor is informational rather than load-bearing:
	// nothing about the import transform is decided from it.
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
		return true; // only a "matches" claim is a claim about these two rectangles
	}
	if (!Manifest.bHasDrape || !Manifest.bHasHeightmap)
	{
		return true; // nothing to compare it against
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
		// The one shape a user has to act on. Until this branch existed the string was opaque and a
		// partially-covering drape imported silently misaligned — the failure looked like nothing
		// at all. The measured numbers lead, because they are what tells a user whether the gap is
		// a rounding edge or half the site.
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
		// Echoed, not judged. Branching only on the shapes we know shows a user nothing for the
		// shape we do not, which is the failure this whole unit exists to stop repeating.
		return FString::Printf(
			TEXT("Imagery drape alignment (a descriptor this plugin version does not recognise, "
			     "reported unchanged): \"%s\"."),
			*Alignment.Declared);

	default:
		return FString::Printf(TEXT("Imagery drape alignment: \"%s\"."), *Alignment.Declared);
	}
}
