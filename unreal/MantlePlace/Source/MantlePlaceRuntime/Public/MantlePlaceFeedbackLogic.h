// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct MANTLEPLACERUNTIME_API FMantlePlaceFeedbackLogic
{
	static const TCHAR* Source();

	static bool IsValidVersion(const FString& Version);

	static bool IsValidOrderId(const FString& OrderId);

	static bool IsValidRating(const FString& Rating);

	static FString BuildFeedbackUrl(const FString& BaseUrl, const FString& PluginVersion, const FString& HostVersion, const FString& OrderId, const FString& Rating = FString());

	static constexpr int32 MaxAskedOrders = 500;

	static bool ShouldAskAboutImport(bool bImportSucceeded, const FString& OrderId, const TArray<FString>& AskedOrderIds);

	static void RememberAsked(const FString& OrderId, TArray<FString>& AskedOrderIds);
};
