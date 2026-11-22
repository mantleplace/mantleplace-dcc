// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class IHttpRequest;

struct FMantlePlaceClientUpdate
{
	FString Message;

	FString SentVersion;

	FString MinimumVersion;

	FString DownloadUrl;
};

DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnClientUpdateRequiredNative, const FMantlePlaceClientUpdate&);

struct MANTLEPLACERUNTIME_API FMantlePlaceClientLogic
{
	static const TCHAR* HeaderName();

	static const TCHAR* ClientName();

	static const TCHAR* UpdateRequiredCode();

	static const TCHAR* DownloadUrl();

	static FString BuildHeaderValue(const FString& PluginVersion);

	static bool ParseClientUpdateRequired(int32 HttpStatus, const FString& Body, FMantlePlaceClientUpdate& OutUpdate);

	static FString BuildUpdateMessage(const FString& SentVersion, const FString& MinimumVersion);
};

struct MANTLEPLACERUNTIME_API FMantlePlaceClientHttp
{
	static FString PluginVersion();

	static FString HeaderValue();

	static TSharedRef<IHttpRequest, ESPMode::ThreadSafe> CreateRequest(const FString& Verb, const FString& Url);
};
