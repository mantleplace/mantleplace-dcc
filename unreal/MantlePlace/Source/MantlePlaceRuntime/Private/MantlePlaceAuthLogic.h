// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"
#include "MantlePlaceAuthTypes.h"

/**
 * Pure (engine-/network-free) auth logic for the Mantle Place platform (Supabase GoTrue).
 *
 * Everything here is deterministic and headless-testable: URL/body construction, response
 * parsing, token-expiry math, and the auth state machine. The UObject shim
 * (UMantlePlaceAuthSystemBase) owns the impure parts — issuing the HTTP request,
 * wall-clock stamping, and firing Blueprint events. Keep this layer free of HTTP/UObject
 * dependencies so the automation test can exercise it under -nullrhi.
 */

/** Events that drive the auth state machine. Plain enum — not Blueprint-exposed. */
enum class EMantlePlaceAuthEvent : uint8
{
	BeginSignIn,
	SignInSucceeded,
	SignInFailed,
	BeginRefresh,
	RefreshSucceeded,
	RefreshFailed,
	SignOut,
	/** Abort an in-flight browser sign-in (user closed the tab / timeout). Does not latch Failed. */
	Cancel,
	/** Begin a cold-start session restore (refresh a stored token from Unauthenticated/Failed). */
	BeginRestore
};

/** Fields parsed from an OAuth redirect callback query string. */
struct FMantlePlaceAuthCallback
{
	FString Code;
	FString State;
	FString Error;
	FString ErrorDescription;
};

struct FMantlePlaceAuthLogic
{
	/** GoTrue's default access-token lifetime (seconds); applied when a token body omits or zeroes
	 *  expires_in, so a fresh sign-in never stamps an already-expired session (audit finding F1). */
	static constexpr int32 DefaultAccessTokenLifetimeSeconds = 3600;

	/** Early-refresh margin (seconds) applied by IsExpired (HPS-11: "at least 60 seconds"). A
	 *  value, not a policy knob — request-gating call sites read this, they do not choose their own. */
	static constexpr int32 ExpirySkewSeconds = 60;

	/** Strip whitespace and any trailing '/' from a configured base URL. */
	static FString NormalizeBaseUrl(const FString& BaseUrl);

	/**
	 * True if BaseUrl is a usable platform base: an http(s) scheme followed by a non-empty host.
	 * Rejects scheme-only values like "https:" or "https://" (a half-typed config that would
	 * otherwise build the hostless URL "https:/auth/v1/token" and DNS-fail at request time).
	 */
	static bool IsValidBaseUrl(const FString& BaseUrl);

	/** Supabase GoTrue endpoints (password / refresh-token grants). */
	static FString BuildPasswordGrantUrl(const FString& BaseUrl);
	static FString BuildRefreshGrantUrl(const FString& BaseUrl);

	/** JSON request bodies (condensed). */
	static FString BuildPasswordGrantBody(const FString& Email, const FString& Password);
	static FString BuildRefreshGrantBody(const FString& RefreshToken);

	/**
	 * Parse a successful GoTrue token response. Returns true and fills OutTokens on success.
	 * On failure returns false and fills OutError (falls back to error-body parsing).
	 */
	static bool ParseTokenResponse(const FString& JsonStr, FMantlePlaceAuthTokens& OutTokens, FString& OutError);

	/**
	 * Parse a GoTrue error body into a human-readable message. Tries the known key variants
	 * (error_description / msg / message / error_code / error). Returns false if none present.
	 */
	static bool ParseErrorResponse(const FString& JsonStr, FString& OutError);

	/**
	 * True if a token expiring at ExpiresAtUtc should be treated as expired at NowUtc,
	 * applying ExpirySkewSeconds of early-refresh margin.
	 */
	static bool IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc);

	/**
	 * Choose the refresh token to keep after a token grant: the new one if present, else the prior.
	 * GoTrue normally rotates refresh_token on every grant, but a response that omits it must not
	 * wipe the cached token (which would strand the session with no way to refresh).
	 */
	static FString ChooseRefreshToken(const FString& NewRefreshToken, const FString& PriorRefreshToken);

	/** Pure state-machine transition. Unknown/illegal transitions return Current unchanged. */
	static EMantlePlaceAuthState NextState(EMantlePlaceAuthState Current, EMantlePlaceAuthEvent Event);

	//~ ----- OAuth 2.0 Authorization Code Flow + PKCE (RFC 8252 / RFC 7636) -----

	/** base64url (RFC 4648 §5, no '=' padding) of a raw byte buffer. */
	static FString Base64UrlEncode(const TArray<uint8>& Bytes);

	/** A PKCE code_verifier: base64url(no-pad) of CSPRNG bytes (RFC 7636 §4.1; 32 bytes → 43 chars). */
	static FString MakeCodeVerifier(const TArray<uint8>& RandomBytes);

	/** A PKCE S256 code_challenge: base64url(SHA256(ASCII(code_verifier))) (RFC 7636 §4.2). */
	static FString MakeCodeChallengeS256(const FString& CodeVerifier);

	/** The loopback redirect URI for a concrete bound port, e.g. http://127.0.0.1:51000/callback. */
	static FString BuildLoopbackRedirectUri(int32 Port, const FString& CallbackPath);

	/**
	 * How the callback port is chosen.
	 *
	 * Ephemeral is the default and the only one that is robust: Windows reserves ~100-port blocks
	 * for Hyper-V/WinNAT that move across reboots, a bind into one is refused although nothing is
	 * listening, and HTTP.SYS reports that refusal as "in use" — so a declared list fails
	 * unpredictably AND misdiagnoses itself. 51000-51009 sat entirely inside one such block. A
	 * 512-wide stride dodged that particular block but was still guessing against a moving target,
	 * and it still made every host on the machine share one finite list.
	 */
	enum class ELoopbackPortMode : uint8
	{
		/** The OS assigns the port. No configuration, no collisions, no reserved ranges. */
		Ephemeral,

		/** Explicit ports, tried in order — an opt-in override for a site that pins redirect_uri. */
		DeclaredList,
	};

	/** Empty configuration means the OS picks; any configured port switches to the declared list. */
	static ELoopbackPortMode ResolveLoopbackPortMode(const TArray<int32>& ConfiguredPorts);

	/**
	 * Acquire an OS-assigned port: propose one, try to bind it, and on failure propose a FRESH one.
	 *
	 * Re-proposing rather than retrying the same number is the whole mechanism. The proposal closes
	 * its probe socket before the real listener binds, so the port can be taken in between; the
	 * allocator has already moved on by the next call, so attempt two is a different port instead
	 * of a re-run of the lost race.
	 *
	 * Both seams are injected for the same reason SelectLoopbackPort's is: the real probe needs a
	 * socket subsystem and the real acquire needs an HTTP listener, and neither exists in a headless
	 * logic test. Returns false and leaves OutPort untouched when every attempt fails.
	 */
	static bool AcquireEphemeralLoopbackPort(
	    TFunctionRef<bool(int32& /*OutProposedPort*/)> ProposePort,
	    TFunctionRef<bool(int32 /*Port*/)> TryAcquire,
	    int32 MaxAttempts,
	    int32& OutPort);

	/**
	 * Try each port in order until TryAcquire reports success; fills OutPort with the winner.
	 * Returns false (leaving OutPort untouched) when every candidate fails.
	 *
	 * Acquisition is injected so this stays pure and headless-testable — the shim passes a lambda
	 * that really binds a listener. That seam exists because the un-injected version of this loop
	 * was dead code that never fell through, and nothing could observe it.
	 */
	static bool SelectLoopbackPort(const TArray<int32>& Ports,
		TFunctionRef<bool(int32 /*Port*/)> TryAcquire, int32& OutPort);

	/**
	 * Build the system-browser authorize URL the user is sent to. WebLoginBaseUrl is the
	 * mantle.place native-login route; the PKCE + redirect query params are appended
	 * (percent-encoded), reusing WebLoginBaseUrl's existing '?' query if it has one.
	 */
	static FString BuildAuthorizeUrl(const FString& WebLoginBaseUrl, const FString& RedirectUri,
		const FString& CodeChallenge, const FString& State);

	/** Supabase-direct PKCE token-exchange endpoint (used when no web TokenEndpointUrl is configured). */
	static FString BuildPkceTokenUrl(const FString& BaseUrl);

	/** PKCE code→token exchange JSON body: {"auth_code":..,"code_verifier":..}. */
	static FString BuildPkceTokenBody(const FString& AuthCode, const FString& CodeVerifier);

	/**
	 * Parse a redirect callback query ("code=..&state=.." or "error=..&error_description=..").
	 * Tolerates a leading '?' or a full URL. Returns true if any recognized field was found.
	 */
	static bool ParseCallbackQuery(const FString& RawQuery, FMantlePlaceAuthCallback& OutCallback);

	/** CSRF guard: true only if Expected is non-empty and exactly equals Received. */
	static bool IsStateValid(const FString& Expected, const FString& Received);

	/** Static HTML the loopback server returns to the browser tab after the redirect. */
	static FString BuildBrowserSuccessHtml();
	static FString BuildBrowserErrorHtml(const FString& Message);
};
