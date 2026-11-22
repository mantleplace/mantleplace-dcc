// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceFeedbackLogic.h"

#include "MantlePlaceAuthLogic.h"
#include "MantlePlaceVaultLogic.h"

namespace
{
	bool IsAsciiDigit(TCHAR C)
	{
		return C >= TEXT('0') && C <= TEXT('9');
	}

	bool IsAsciiHex(TCHAR C)
	{
		return IsAsciiDigit(C) || (C >= TEXT('a') && C <= TEXT('f')) || (C >= TEXT('A') && C <= TEXT('F'));
	}
}

const TCHAR* FMantlePlaceFeedbackLogic::Source()
{
	return TEXT("dcc-unreal");
}

bool FMantlePlaceFeedbackLogic::IsValidVersion(const FString& Version)
{
	int32 Groups = 0;
	int32 DigitsInGroup = 0;
	for (int32 i = 0; i < Version.Len(); ++i)
	{
		const TCHAR C = Version[i];
		if (IsAsciiDigit(C))
		{
			if (DigitsInGroup == 0)
			{
				++Groups;
			}
			++DigitsInGroup;
		}
		else if (C == TEXT('.') && DigitsInGroup > 0)
		{
			DigitsInGroup = 0;
		}
		else
		{
			return false;
		}
	}
	return DigitsInGroup > 0 && Groups >= 1 && Groups <= 4;
}

bool FMantlePlaceFeedbackLogic::IsValidOrderId(const FString& OrderId)
{
	if (OrderId.Len() != 36)
	{
		return false;
	}
	for (int32 i = 0; i < OrderId.Len(); ++i)
	{
		const bool bHyphenSlot = (i == 8 || i == 13 || i == 18 || i == 23);
		if (bHyphenSlot ? (OrderId[i] != TEXT('-')) : !IsAsciiHex(OrderId[i]))
		{
			return false;
		}
	}
	return true;
}

bool FMantlePlaceFeedbackLogic::IsValidRating(const FString& Rating)
{
	return Rating.Equals(TEXT("up"), ESearchCase::CaseSensitive) || Rating.Equals(TEXT("down"), ESearchCase::CaseSensitive);
}

FString FMantlePlaceFeedbackLogic::BuildFeedbackUrl(const FString& BaseUrl, const FString& PluginVersion, const FString& HostVersion, const FString& OrderId, const FString& Rating)
{
	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(BaseUrl))
	{
		return FString();
	}

	FString Url = FMantlePlaceVaultLogic::NormalizeBaseUrl(BaseUrl) + TEXT("/feedback?source=") + Source();
	if (IsValidVersion(PluginVersion))
	{
		Url += TEXT("&v=") + PluginVersion;
	}
	if (IsValidVersion(HostVersion))
	{
		Url += TEXT("&hv=") + HostVersion;
	}
	if (IsValidOrderId(OrderId))
	{
		Url += TEXT("&order=") + OrderId;
	}
	if (IsValidRating(Rating))
	{
		Url += TEXT("&rating=") + Rating;
	}
	return Url;
}

bool FMantlePlaceFeedbackLogic::ShouldAskAboutImport(bool bImportSucceeded, const FString& OrderId, const TArray<FString>& AskedOrderIds)
{
	if (!bImportSucceeded || !IsValidOrderId(OrderId))
	{
		return false;
	}
	for (const FString& Asked : AskedOrderIds)
	{
		if (Asked.Equals(OrderId, ESearchCase::IgnoreCase))
		{
			return false;
		}
	}
	return true;
}

void FMantlePlaceFeedbackLogic::RememberAsked(const FString& OrderId, TArray<FString>& AskedOrderIds)
{
	if (!IsValidOrderId(OrderId) || !ShouldAskAboutImport(true, OrderId, AskedOrderIds))
	{
		return;
	}
	AskedOrderIds.Add(OrderId.ToLower());
	if (AskedOrderIds.Num() > MaxAskedOrders)
	{
		AskedOrderIds.RemoveAt(0, AskedOrderIds.Num() - MaxAskedOrders);
	}
}
