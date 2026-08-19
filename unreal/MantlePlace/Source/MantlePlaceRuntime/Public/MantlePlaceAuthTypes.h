// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceAuthTypes.generated.h"

/**
 * Authentication state for the Mantle Place platform (Supabase GoTrue) auth system.
 * Exposed to Blueprint so the surface (BP child) can react to state changes.
 */
UENUM(BlueprintType)
enum class EMantlePlaceAuthState : uint8
{
	/** No valid session; the user must sign in. */
	Unauthenticated,
	/** A sign-in request is in flight. */
	Authenticating,
	/** A valid (non-expired) access token is cached in memory. */
	Authenticated,
	/** A token-refresh request is in flight. */
	Refreshing,
	/** The last sign-in / refresh attempt failed. */
	Failed
};

/**
 * Cached session tokens. Held in memory only for the lifetime of the auth object —
 * never serialized or written to disk. Plain struct (not a USTRUCT): raw JWTs are
 * intentionally not surfaced to Blueprint or the reflection system.
 */
struct FMantlePlaceAuthTokens
{
	FString AccessToken;
	FString RefreshToken;
	FString UserId;
	/** Lifetime of AccessToken in seconds, as reported by GoTrue (relative; clock-free). */
	int32 ExpiresInSeconds = 0;

	bool IsValid() const { return !AccessToken.IsEmpty(); }

	void Reset()
	{
		AccessToken.Empty();
		RefreshToken.Empty();
		UserId.Empty();
		ExpiresInSeconds = 0;
	}
};
