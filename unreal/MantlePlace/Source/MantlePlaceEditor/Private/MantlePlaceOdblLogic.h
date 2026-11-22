// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

enum class EMantlePlaceOdblRole : uint8
{
	Database,
	ProducedWork,
	None,
};

struct FMantlePlaceFileSource
{
	FString Name;
	FString License;
};

struct FMantlePlaceFileLicensing
{
	EMantlePlaceOdblRole Role = EMantlePlaceOdblRole::Database;
	FString License;
	TArray<FMantlePlaceFileSource> Sources;
};

struct FMantlePlaceAttributionSource
{
	FString ProviderId;
	FString AttributionText;
	FString License;
	FString LicenseUrl;
};

namespace MantlePlaceOdblLogic
{
EMantlePlaceOdblRole ParseRole(bool bStated, const FString& Value);

FString RoleName(EMantlePlaceOdblRole Role);

const FMantlePlaceFileLicensing* FindEntry(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path);

EMantlePlaceOdblRole RoleOfPath(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path);

EMantlePlaceOdblRole RoleOfPaths(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths);

bool NeedsCredit(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths);

FString CreditLine(const FMantlePlaceAttributionSource& Source);

TArray<FString> CreditLines(
	const TArray<FMantlePlaceAttributionSource>& Attribution,
	const TMap<FString, FMantlePlaceFileLicensing>& Entries,
	const TArray<FString>& Paths);

TArray<FString> CreditTags(const TArray<FString>& Lines);

TArray<FString> CreditLinesFromTags(const TArray<FString>& Tags);

struct FHandoverItem
{
	FString Identity;
	bool bOdbl = false;
	FString EquivalenceGroup;
	TArray<FString> CreditLines;
};

enum class EHandoverDecision : uint8
{
	Keep,
	Remove,
	Swap,
};

struct FHandoverCredit
{
	FString Identity;
	TArray<FString> Lines;
};

struct FHandoverPlan
{
	TArray<EHandoverDecision> Decisions;
	TArray<FHandoverCredit> Credits;
};

FHandoverPlan PlanHandover(const TArray<FHandoverItem>& Items);

FString CreditText(const TArray<FString>& Lines);
}
