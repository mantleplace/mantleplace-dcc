// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceOdblLogic.h"

#include "MantlePlaceImportNaming.h"

#include "Containers/StringConv.h" // StringConv::IsHighSurrogate

namespace
{
bool IsOdblLicense(const FString& License)
{
	return License.Contains(TEXT("ODbL"), ESearchCase::IgnoreCase);
}
}

EMantlePlaceOdblRole MantlePlaceOdblLogic::ParseRole(bool bStated, const FString& Value)
{
	if (bStated)
	{
		if (Value == TEXT("produced-work")) { return EMantlePlaceOdblRole::ProducedWork; }
		if (Value == TEXT("none")) { return EMantlePlaceOdblRole::None; }
	}
	// `database`, an absent role and a role this reader has not met: the stricter duty.
	return EMantlePlaceOdblRole::Database;
}

FString MantlePlaceOdblLogic::RoleName(EMantlePlaceOdblRole Role)
{
	switch (Role)
	{
	case EMantlePlaceOdblRole::ProducedWork: return TEXT("produced-work");
	case EMantlePlaceOdblRole::None:         return TEXT("none");
	case EMantlePlaceOdblRole::Database:
	default:                                 return TEXT("database");
	}
}

const FMantlePlaceFileLicensing* MantlePlaceOdblLogic::FindEntry(
	const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path)
{
	if (const FMantlePlaceFileLicensing* Exact = Entries.Find(Path))
	{
		return Exact;
	}
	// A path is matched regardless of case (MPB 2.0, format.md 10.3 rule 6). A bundle never holds two
	// entries that differ only by case, so the first match is the only one.
	for (const TPair<FString, FMantlePlaceFileLicensing>& Pair : Entries)
	{
		if (Pair.Key.Equals(Path, ESearchCase::IgnoreCase))
		{
			return &Pair.Value;
		}
	}
	return nullptr;
}

EMantlePlaceOdblRole MantlePlaceOdblLogic::RoleOfPath(
	const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path)
{
	const FMantlePlaceFileLicensing* Entry = FindEntry(Entries, Path);
	return Entry != nullptr ? Entry->Role : EMantlePlaceOdblRole::Database;
}

EMantlePlaceOdblRole MantlePlaceOdblLogic::RoleOfPaths(
	const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths)
{
	bool bProducedWork = false;
	for (const FString& Path : Paths)
	{
		switch (RoleOfPath(Entries, Path))
		{
		case EMantlePlaceOdblRole::Database:     return EMantlePlaceOdblRole::Database;
		case EMantlePlaceOdblRole::ProducedWork: bProducedWork = true; break;
		case EMantlePlaceOdblRole::None:         break;
		}
	}
	return bProducedWork ? EMantlePlaceOdblRole::ProducedWork : EMantlePlaceOdblRole::None;
}

bool MantlePlaceOdblLogic::NeedsCredit(
	const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths)
{
	if (RoleOfPaths(Entries, Paths) == EMantlePlaceOdblRole::Database)
	{
		return true;
	}
	for (const FString& Path : Paths)
	{
		const FMantlePlaceFileLicensing* Entry = FindEntry(Entries, Path);
		if (Entry == nullptr)
		{
			continue;
		}
		for (const FMantlePlaceFileSource& Source : Entry->Sources)
		{
			if (IsOdblLicense(Source.License))
			{
				return true;
			}
		}
	}
	return false;
}

FString MantlePlaceOdblLogic::CreditLine(const FMantlePlaceAttributionSource& Source)
{
	// A line break inside a field becomes a space, so one source is always one line.
	auto OneLine = [](FString Text)
	{
		Text.ReplaceInline(TEXT("\r\n"), TEXT(" "));
		Text.ReplaceInline(TEXT("\n"), TEXT(" "));
		Text.ReplaceInline(TEXT("\r"), TEXT(" "));
		return Text.TrimStartAndEnd();
	};
	const FString Name = !Source.AttributionText.TrimStartAndEnd().IsEmpty() ? Source.AttributionText : Source.ProviderId;
	const FString Separator = FString::Printf(TEXT(" %c "), static_cast<TCHAR>(0x2014));
	TArray<FString> Fields;
	for (const FString& Field : { Name, Source.License, Source.LicenseUrl })
	{
		const FString Clean = OneLine(Field);
		if (!Clean.IsEmpty())
		{
			Fields.Add(Clean);
		}
	}
	return FString::Join(Fields, *Separator);
}

TArray<FString> MantlePlaceOdblLogic::CreditLines(
	const TArray<FMantlePlaceAttributionSource>& Attribution,
	const TMap<FString, FMantlePlaceFileLicensing>& Entries,
	const TArray<FString>& Paths)
{
	TArray<FString> Lines;
	for (const FMantlePlaceAttributionSource& Source : Attribution)
	{
		Lines.AddUnique(CreditLine(Source));
	}
	Lines.RemoveAll([](const FString& Line) { return Line.IsEmpty(); });
	if (Lines.IsEmpty())
	{
		for (const FString& Path : Paths)
		{
			const FMantlePlaceFileLicensing* Entry = FindEntry(Entries, Path);
			if (Entry == nullptr)
			{
				continue;
			}
			for (const FMantlePlaceFileSource& Source : Entry->Sources)
			{
				if (IsOdblLicense(Source.License))
				{
					FMantlePlaceAttributionSource AsSource;
					AsSource.AttributionText = Source.Name;
					AsSource.License = Source.License;
					Lines.AddUnique(CreditLine(AsSource));
				}
			}
		}
	}
	return Lines;
}

TArray<FString> MantlePlaceOdblLogic::CreditTags(const TArray<FString>& Lines)
{
	constexpr int32 MaxTagChars = NAME_SIZE - 1;
	const FString WholePrefix = MantlePlaceImportNaming::CreditLineTagPrefix();
	const FString PartPrefix = MantlePlaceImportNaming::CreditPartTagPrefix();

	TArray<FString> Tags;
	for (int32 LineIndex = 0; LineIndex < Lines.Num(); ++LineIndex)
	{
		const FString& Line = Lines[LineIndex];
		if (WholePrefix.Len() + Line.Len() <= MaxTagChars)
		{
			Tags.Add(WholePrefix + Line);
			continue;
		}

		// Room for the widest header this line can need, so every part of it is cut to one length.
		const int32 Room = MaxTagChars - PartPrefix.Len()
			- FString::Printf(TEXT("%d.%d.%d:"), LineIndex, Line.Len(), Line.Len()).Len();
		TArray<FString> Parts;
		for (int32 Start = 0; Start < Line.Len();)
		{
			int32 Count = FMath::Min(Room, Line.Len() - Start);
			// Never end a part between the two halves of a surrogate pair.
			if (Start + Count < Line.Len() && Count > 1 && StringConv::IsHighSurrogate(Line[Start + Count - 1]))
			{
				--Count;
			}
			Parts.Add(Line.Mid(Start, Count));
			Start += Count;
		}
		for (int32 PartIndex = 0; PartIndex < Parts.Num(); ++PartIndex)
		{
			Tags.Add(FString::Printf(TEXT("%s%d.%d.%d:%s"), *PartPrefix, LineIndex, PartIndex, Parts.Num(), *Parts[PartIndex]));
		}
	}
	return Tags;
}

TArray<FString> MantlePlaceOdblLogic::CreditLinesFromTags(const TArray<FString>& Tags)
{
	const FString WholePrefix = MantlePlaceImportNaming::CreditLineTagPrefix();
	const FString PartPrefix = MantlePlaceImportNaming::CreditPartTagPrefix();

	// One slot per line, in the order its first tag appears; a long line's parts fill its slot.
	struct FSlot
	{
		FString Whole;
		bool bParts = false;
		FString PartKey;       // `<line>` of the parts, so two long lines never share a slot
		TArray<FString> Parts; // by part index; an empty entry is a part not (yet) seen
		TArray<bool> Seen;
	};
	TArray<FSlot> Slots;
	for (const FString& Tag : Tags)
	{
		if (Tag.StartsWith(WholePrefix, ESearchCase::CaseSensitive))
		{
			Slots.AddDefaulted_GetRef().Whole = Tag.Mid(WholePrefix.Len());
			continue;
		}
		if (!Tag.StartsWith(PartPrefix, ESearchCase::CaseSensitive))
		{
			continue;
		}
		const FString Rest = Tag.Mid(PartPrefix.Len());
		int32 Colon = INDEX_NONE;
		if (!Rest.FindChar(TEXT(':'), Colon))
		{
			continue;
		}
		TArray<FString> Header;
		Rest.Left(Colon).ParseIntoArray(Header, TEXT("."));
		if (Header.Num() != 3 || !Header[1].IsNumeric() || !Header[2].IsNumeric())
		{
			continue;
		}
		const int32 PartIndex = FCString::Atoi(*Header[1]);
		const int32 PartCount = FCString::Atoi(*Header[2]);
		if (PartCount < 1 || PartIndex < 0 || PartIndex >= PartCount)
		{
			continue;
		}
		FSlot* Slot = Slots.FindByPredicate([&Header](const FSlot& Existing) { return Existing.bParts && Existing.PartKey == Header[0]; });
		if (Slot == nullptr)
		{
			Slot = &Slots.AddDefaulted_GetRef();
			Slot->bParts = true;
			Slot->PartKey = Header[0];
			Slot->Parts.SetNum(PartCount);
			Slot->Seen.Init(false, PartCount);
		}
		if (Slot->Parts.Num() != PartCount)
		{
			continue; // parts that disagree on their count belong to no line this reader can trust
		}
		Slot->Parts[PartIndex] = Rest.Mid(Colon + 1);
		Slot->Seen[PartIndex] = true;
	}

	TArray<FString> Lines;
	for (const FSlot& Slot : Slots)
	{
		if (!Slot.bParts)
		{
			Lines.AddUnique(Slot.Whole);
		}
		else if (!Slot.Seen.Contains(false))
		{
			Lines.AddUnique(FString::Join(Slot.Parts, TEXT("")));
		}
	}
	return Lines;
}

MantlePlaceOdblLogic::FHandoverPlan MantlePlaceOdblLogic::PlanHandover(const TArray<FHandoverItem>& Items)
{
	FHandoverPlan Plan;
	for (const FHandoverItem& Item : Items)
	{
		if (!Item.bOdbl)
		{
			Plan.Decisions.Add(EHandoverDecision::Keep);
			continue;
		}
		bool bStandIn = false;
		if (!Item.EquivalenceGroup.IsEmpty())
		{
			for (const FHandoverItem& Other : Items)
			{
				if (!Other.bOdbl && Other.Identity == Item.Identity && Other.EquivalenceGroup == Item.EquivalenceGroup)
				{
					bStandIn = true;
					break;
				}
			}
		}
		Plan.Decisions.Add(bStandIn ? EHandoverDecision::Swap : EHandoverDecision::Remove);
	}

	for (const FHandoverItem& Item : Items)
	{
		if (Item.CreditLines.IsEmpty())
		{
			continue;
		}
		FHandoverCredit* Credit = Plan.Credits.FindByPredicate(
			[&Item](const FHandoverCredit& Existing) { return Existing.Identity == Item.Identity; });
		if (Credit == nullptr)
		{
			Credit = &Plan.Credits.AddDefaulted_GetRef();
			Credit->Identity = Item.Identity;
		}
		for (const FString& Line : Item.CreditLines)
		{
			Credit->Lines.AddUnique(Line);
		}
	}
	return Plan;
}

FString MantlePlaceOdblLogic::CreditText(const TArray<FString>& Lines)
{
	return FString::Join(Lines, TEXT("\n"));
}
