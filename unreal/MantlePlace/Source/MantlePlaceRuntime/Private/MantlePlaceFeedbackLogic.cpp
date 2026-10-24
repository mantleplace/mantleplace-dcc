// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceFeedbackLogic.h"

#include "MantlePlaceAuthLogic.h"  // IsValidBaseUrl
#include "MantlePlaceVaultLogic.h" // NormalizeBaseUrl

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
	// ^[0-9]+(\.[0-9]+){0,3}$ : one to four dot-separated runs of digits, none empty.
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
	// 8-4-4-4-12 hexadecimal, hyphens at 8, 13, 18 and 23.
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

FString FMantlePlaceFeedbackLogic::BuildFeedbackUrl(const FString& BaseUrl, const FString& PluginVersion, const FString& HostVersion, const FString& OrderId)
{
	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(BaseUrl))
	{
		return FString();
	}

	// Every value appended below is a fixed word, a validated version (digits and dots) or a
	// validated UUID (hex and hyphens), so none needs percent-encoding.
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
	return Url;
}
