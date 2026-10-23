// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceOdblLogic.h"

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

EMantlePlaceOdblRole MantlePlaceOdblLogic::RoleOfPath(
	const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path)
{
	const FMantlePlaceFileLicensing* Entry = Entries.Find(Path);
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
		const FMantlePlaceFileLicensing* Entry = Entries.Find(Path);
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
			const FMantlePlaceFileLicensing* Entry = Entries.Find(Path);
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
