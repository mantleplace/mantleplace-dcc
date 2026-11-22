// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"
#include "MantlePlaceAuthTypes.h"

enum class EMantlePlaceAuthEvent : uint8
{
	BeginSignIn,
	SignInSucceeded,
	SignInFailed,
	BeginRefresh,
	RefreshSucceeded,
	RefreshFailed,
	SignOut,
	Cancel,
	BeginRestore
};

struct FMantlePlaceAuthCallback
{
	FString Code;
	FString State;
	FString Error;
	FString ErrorDescription;
};

struct FMantlePlaceAuthLogic
{
	static constexpr int32 DefaultAccessTokenLifetimeSeconds = 3600;

	static constexpr int32 ExpirySkewSeconds = 60;

	static FString NormalizeBaseUrl(const FString& BaseUrl);

	static bool IsValidBaseUrl(const FString& BaseUrl);

	static FString BuildRefreshGrantBody(const FString& RefreshToken);

	static bool ParseTokenResponse(const FString& JsonStr, FMantlePlaceAuthTokens& OutTokens, FString& OutError);

	static bool IsDefinitiveRejection(int32 HttpStatus, const FString& Body);

	static bool ParseErrorResponse(const FString& JsonStr, FString& OutError);

	static bool IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc);

	static FString ChooseRefreshToken(const FString& NewRefreshToken, const FString& PriorRefreshToken);

	static EMantlePlaceAuthState NextState(EMantlePlaceAuthState Current, EMantlePlaceAuthEvent Event);

	static FString Base64UrlEncode(const TArray<uint8>& Bytes);

	static FString MakeCodeVerifier(const TArray<uint8>& RandomBytes);

	static FString MakeCodeChallengeS256(const FString& CodeVerifier);

	static FString BuildLoopbackRedirectUri(int32 Port, const FString& CallbackPath);

	enum class ELoopbackPortMode : uint8
	{
		Ephemeral,

		DeclaredList,
	};

	static ELoopbackPortMode ResolveLoopbackPortMode(const TArray<int32>& ConfiguredPorts);

	static bool AcquireEphemeralLoopbackPort(
	    TFunctionRef<bool(int32&)> ProposePort,
	    TFunctionRef<bool(int32)> TryAcquire,
	    int32 MaxAttempts,
	    int32& OutPort);

	static bool SelectLoopbackPort(const TArray<int32>& Ports,
		TFunctionRef<bool(int32)> TryAcquire, int32& OutPort);

	static FString BuildAuthorizeUrl(const FString& WebLoginBaseUrl, const FString& RedirectUri,
		const FString& CodeChallenge, const FString& State);

	static FString BuildPkceTokenBody(const FString& AuthCode, const FString& CodeVerifier);

	static bool ParseCallbackQuery(const FString& RawQuery, FMantlePlaceAuthCallback& OutCallback);

	static bool IsStateValid(const FString& Expected, const FString& Received);

	static FString BuildBrowserSuccessHtml();
	static FString BuildBrowserErrorHtml(const FString& Message);
};
