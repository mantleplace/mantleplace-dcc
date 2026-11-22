// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceDeliveryLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
FString InStandardWords(const FString& Published, const TCHAR* const (*Table)[2], int32 Count)
{
	if (Published.IsEmpty())
	{
		return TEXT("not stated");
	}
	for (int32 Index = 0; Index < Count; ++Index)
	{
		if (Published.Equals(Table[Index][0], ESearchCase::CaseSensitive))
		{
			return Table[Index][1];
		}
	}
	return Published;
}

const TCHAR* const UnitSystemWords[][2] = {
	{ TEXT("metric"), TEXT("Metric") },
	{ TEXT("imperial"), TEXT("Imperial") },
};

const TCHAR* const LinearUnitWords[][2] = {
	{ TEXT("m"), TEXT("metres") },
	{ TEXT("ftUS"), TEXT("US survey feet") },
	{ TEXT("ft"), TEXT("international feet") },
};
}

TArray<FString> FMantlePlaceDeliveryLogic::DescribeDelivery(const FMantlePlaceDeliveryFacts& Delivery)
{
	TArray<FString> Lines;
	if (!Delivery.bDeclared)
	{
		return Lines;
	}

	const FString Crs = !Delivery.Label.IsEmpty() ? Delivery.Label
		: Delivery.bHasHorizontalEpsg ? FString::Printf(TEXT("EPSG:%d"), Delivery.HorizontalEpsg)
		: FString(TEXT("no delivery CRS"));
	Lines.Add(FString::Printf(TEXT("Bundle delivery: %s · %s · %s"),
		*InStandardWords(Delivery.UnitSystem, UnitSystemWords, UE_ARRAY_COUNT(UnitSystemWords)),
		*InStandardWords(Delivery.LinearUnit, LinearUnitWords, UE_ARRAY_COUNT(LinearUnitWords)),
		*Crs));

	if (Delivery.LinearUnit.Equals(TEXT("ftUS"), ESearchCase::CaseSensitive)
		|| Delivery.LinearUnit.Equals(TEXT("ft"), ESearchCase::CaseSensitive))
	{
		Lines.Add(TEXT("Unreal content: metres, engine-native. The delivered GIS and CAD files are in feet."));
	}
	return Lines;
}
