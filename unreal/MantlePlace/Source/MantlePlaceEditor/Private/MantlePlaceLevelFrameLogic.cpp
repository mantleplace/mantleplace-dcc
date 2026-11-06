// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLevelFrameLogic.h"

namespace
{
const TCHAR* const FrameVersion = TEXT("v1");
const TCHAR* const PlacedWord = TEXT("placed");
const TCHAR* const CentredWord = TEXT("centred");

/** A separator-free field: the separator and the escape character are escaped, nothing else changes. */
FString Escape(const FString& Text)
{
	return Text.Replace(TEXT("%"), TEXT("%25")).Replace(TEXT("|"), TEXT("%7C"));
}

FString Unescape(const FString& Text)
{
	return Text.Replace(TEXT("%7C"), TEXT("|")).Replace(TEXT("%25"), TEXT("%"));
}

FString ShownOrNone(const FString& Text)
{
	return Text.IsEmpty() ? FString(TEXT("(none stated)")) : Text;
}

FMantlePlaceFramePlan MakeCentred(const FMantlePlaceFrameOrigin& Origin, EMantlePlaceFrameRefusal Refusal, const FString& Warning)
{
	FMantlePlaceFramePlan Plan;
	Plan.Outcome = EMantlePlaceFrameOutcome::Centred;
	Plan.Refusal = Refusal;
	Plan.Warning = Warning;
	FMantlePlaceFrameRecord Record;
	Record.bPlaced = false;
	Record.Origin = Origin;
	Plan.FrameValue = FMantlePlaceLevelFrameLogic::EncodeFrame(Record);
	return Plan;
}
}

FString FMantlePlaceLevelFrameLogic::EncodeFrame(const FMantlePlaceFrameRecord& Record)
{
	return FString::Printf(
		TEXT("%s|%s|%s|%s|%.4f|%.4f|%.4f|%.4f"), FrameVersion, Record.bPlaced ? PlacedWord : CentredWord,
		*Escape(Record.Origin.Crs), *Escape(Record.Origin.VerticalDatum), Record.Origin.EastingM,
		Record.Origin.NorthingM, Record.OffsetEastM, Record.OffsetNorthM);
}

bool FMantlePlaceLevelFrameLogic::DecodeFrame(const FString& Value, FMantlePlaceFrameRecord& OutRecord)
{
	TArray<FString> Parts;
	Value.ParseIntoArray(Parts, TEXT("|"), /*bCullEmpty*/ false);
	if (Parts.Num() != 8 || Parts[0] != FrameVersion)
	{
		return false;
	}
	if (Parts[1] != PlacedWord && Parts[1] != CentredWord)
	{
		return false;
	}
	for (int32 Index = 4; Index < 8; ++Index)
	{
		if (!Parts[Index].IsNumeric())
		{
			return false;
		}
	}
	FMantlePlaceFrameRecord Record;
	Record.bPlaced = Parts[1] == PlacedWord;
	Record.Origin.Crs = Unescape(Parts[2]);
	Record.Origin.VerticalDatum = Unescape(Parts[3]);
	Record.Origin.EastingM = FCString::Atod(*Parts[4]);
	Record.Origin.NorthingM = FCString::Atod(*Parts[5]);
	Record.Origin.bStated = true;
	Record.OffsetEastM = FCString::Atod(*Parts[6]);
	Record.OffsetNorthM = FCString::Atod(*Parts[7]);
	OutRecord = Record;
	return true;
}

FMantlePlaceFramePlan FMantlePlaceLevelFrameLogic::Plan(
	const FString& ThisIdentity, const FMantlePlaceFrameOrigin& ThisOrigin,
	const TArray<FMantlePlaceLevelImportTag>& InLevel)
{
	// One record per OTHER import: an import's actors all carry the same frame tag, and the import being
	// replaced is not a witness to where the anchor is. Sorted by identity so the choice below does not
	// depend on the order the level happens to list its actors in.
	TMap<FString, FMantlePlaceFrameRecord> PlacedByIdentity;
	TSet<FString> OtherIdentities;
	FMantlePlaceFrameRecord OwnWitness;
	bool bHaveOwn = false;
	for (const FMantlePlaceLevelImportTag& Found : InLevel)
	{
		if (Found.Identity == ThisIdentity)
		{
			// Not a witness while another placed import exists, but the last word on the anchor when
			// none does: see OwnWitness below.
			FMantlePlaceFrameRecord Own;
			if (!bHaveOwn && DecodeFrame(Found.FrameValue, Own) && Own.bPlaced)
			{
				OwnWitness = Own;
				bHaveOwn = true;
			}
			continue;
		}
		OtherIdentities.Add(Found.Identity);
		FMantlePlaceFrameRecord Record;
		if (DecodeFrame(Found.FrameValue, Record) && Record.bPlaced)
		{
			PlacedByIdentity.FindOrAdd(Found.Identity) = Record;
		}
	}
	const int32 NotPlaced = OtherIdentities.Num() - PlacedByIdentity.Num();

	const bool bOriginUsable = ThisOrigin.bStated && !ThisOrigin.Crs.IsEmpty();

	// Nothing placed to measure against: this import is the anchor (or, with no origin to anchor on, is
	// centred as it always was, and anchors nothing).
	// The one exception: this bundle's own placed actors carry an offset and no other placed import is
	// left (the anchor bundle was deleted). Its own tag still says where the anchor was, so it keeps its
	// offset rather than becoming the anchor and jumping to the world origin.
	const bool bUseOwnWitness = PlacedByIdentity.Num() == 0 && bHaveOwn
		&& (OwnWitness.OffsetEastM != 0.0 || OwnWitness.OffsetNorthM != 0.0);
	if (bUseOwnWitness)
	{
		PlacedByIdentity.Add(ThisIdentity, OwnWitness);
	}

	if (PlacedByIdentity.Num() == 0)
	{
		if (!bOriginUsable)
		{
			return MakeCentred(ThisOrigin, EMantlePlaceFrameRefusal::None, FString());
		}
		FMantlePlaceFramePlan Result;
		Result.Outcome = EMantlePlaceFrameOutcome::Anchor;
		FMantlePlaceFrameRecord Record;
		Record.bPlaced = true;
		Record.Origin = ThisOrigin;
		Result.FrameValue = EncodeFrame(Record);
		if (NotPlaced > 0)
		{
			Result.Note = FString::Printf(
				TEXT("This import is now the level's anchor. %d other Mantle Place import(s) in the level were not "
					 "placed on a shared frame, so they stay where they are."),
				NotPlaced);
		}
		return Result;
	}

	TArray<FString> Keys;
	PlacedByIdentity.GetKeys(Keys);
	Keys.Sort();
	const FMantlePlaceFrameRecord& Witness = PlacedByIdentity[Keys[0]];
	const FMantlePlaceFrameOrigin& Anchor = Witness.Origin;
	const double AnchorEastingM = Anchor.EastingM - Witness.OffsetEastM;
	const double AnchorNorthingM = Anchor.NorthingM - Witness.OffsetNorthM;

	const FString Intro = TEXT("WARNING: this bundle was imported centred on the world origin, not beside the one already in the level: ");
	const FString Outro = TEXT(" Move it by hand if it belongs beside the other.");

	if (!bOriginUsable)
	{
		return MakeCentred(ThisOrigin, EMantlePlaceFrameRefusal::OriginUnstated,
			Intro + TEXT("its manifest states no georeference origin or no projected CRS.") + Outro);
	}
	// Case-sensitive on purpose: FString's == ignores case, and "epsg:32613" is not the string the
	// manifest publishes. Equal text is the only match.
	if (!ThisOrigin.Crs.Equals(Anchor.Crs, ESearchCase::CaseSensitive))
	{
		return MakeCentred(ThisOrigin, EMantlePlaceFrameRefusal::CrsDiffers,
			Intro + FString::Printf(TEXT("its projected CRS is %s and the level's is %s. The plugin offsets only "
										 "bundles stated on the same CRS string, and converts nothing."),
				*ThisOrigin.Crs, *Anchor.Crs) + Outro);
	}
	if (ThisOrigin.VerticalDatum.IsEmpty() || Anchor.VerticalDatum.IsEmpty()
		|| !ThisOrigin.VerticalDatum.Equals(Anchor.VerticalDatum, ESearchCase::CaseSensitive))
	{
		return MakeCentred(ThisOrigin, EMantlePlaceFrameRefusal::DatumDiffers,
			Intro + FString::Printf(TEXT("its vertical datum is %s and the level's is %s. The plugin offsets only "
										 "bundles stated on the same vertical datum, and converts nothing."),
				*ShownOrNone(ThisOrigin.VerticalDatum), *ShownOrNone(Anchor.VerticalDatum)) + Outro);
	}

	const double EastM = ThisOrigin.EastingM - AnchorEastingM;
	const double NorthM = ThisOrigin.NorthingM - AnchorNorthingM;
	if (FMath::Max(FMath::Abs(EastM), FMath::Abs(NorthM)) > MaxOffsetM)
	{
		return MakeCentred(ThisOrigin, EMantlePlaceFrameRefusal::TooFar,
			Intro + FString::Printf(TEXT("it is %.0f m east and %.0f m north of the level's first import, beyond the "
										 "%.0f m the plugin places by offset."),
				EastM, NorthM, MaxOffsetM) + Outro);
	}

	FMantlePlaceFramePlan Result;
	Result.Outcome = EMantlePlaceFrameOutcome::Offset;
	Result.OffsetEastM = EastM;
	Result.OffsetNorthM = NorthM;
	FMantlePlaceFrameRecord Record;
	Record.bPlaced = true;
	Record.Origin = ThisOrigin;
	Record.OffsetEastM = EastM;
	Record.OffsetNorthM = NorthM;
	Result.FrameValue = EncodeFrame(Record);
	return Result;
}
