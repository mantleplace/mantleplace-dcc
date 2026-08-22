// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "Containers/Ticker.h"
#include "MantlePlaceAuthTypes.h"
#include "MantlePlaceAuthSystemBase.generated.h"

class IHttpRequest;
class IHttpResponse;
// Forward-declared so this PUBLIC header stays free of HTTPServer / private-store includes
// (HTTPServer is a private module dependency; the secret store is a private header).
class IHttpRouter;
struct FHttpRouteHandleInternal;
class IMantlePlaceSecretStore;

/**
 * Native (C++) broadcast of auth-state transitions. The four auth events on this class are
 * BlueprintImplementableEvents (for the BP child), which C++/Slate cannot subscribe to; editor
 * observers (the vault orchestrator/panel) bind this instead to react to sign-in/out completing.
 * Fired from the single SetAuthState chokepoint, so it covers every transition.
 */
DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnAuthStateChangedNative, EMantlePlaceAuthState /*NewState*/);

/**
 * C++ base for the Mantle Place auth system.
 *
 * Owns the auth LOGIC: it signs in against the Mantle Place platform API (Supabase
 * GoTrue) over HTTP with a JWT, caches the session token in memory, and exposes auth
 * state. The deterministic core (URL/body construction, response parsing, expiry, the
 * state machine) lives in the headless-testable FMantlePlaceAuthLogic; this class
 * is the thin impure shim that issues requests and fires Blueprint events.
 *
 * A human reparents a Blueprint child (BP_MantlePlaceAuthSystemBase) onto this base
 * and wires only the surface — which widget shows on failure, success transitions, etc.
 * See Docs/Auth-Reparent.md.
 *
 * Endpoint classification (mirrors the Revit host's MantlePlaceEndpoints):
 * the public mantle.place routes (WebLoginUrl, TokenEndpointUrl) are compiled in — they are
 * public URLs, and a plugin that cannot sign in without a config file is a plugin that cannot
 * sign in. PlatformApiBaseUrl + SupabaseAnonKey have NO default and are not in this plugin:
 * they are hydrated from the consuming project's DefaultGame.ini section
 * [/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase] at packaging time. Sign-in works
 * without them (the browser flow brokers through mantle.place); token refresh/restore is
 * Supabase-direct and reports a named misconfiguration when they are absent. The anon key is
 * a public client key; never place a service-role/secret key here — and never bake either
 * value into the BP child's class defaults, which ship with the plugin.
 */
UCLASS(Blueprintable, config = Game)
class MANTLEPLACERUNTIME_API UMantlePlaceAuthSystemBase : public UObject
{
	GENERATED_BODY()

public:
	/**
	 * Supabase project URL, e.g. https://<ref>.supabase.co (no trailing slash required).
	 * Capture-sensitive: no default here — hydrated from the consuming project's config at
	 * packaging time (see the class comment). Never commit a value into this plugin.
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Auth")
	FString PlatformApiBaseUrl;

	/**
	 * Supabase anon (public) API key, sent as the apikey / bearer header (Supabase-direct calls
	 * only). Capture-sensitive like PlatformApiBaseUrl: no default here, hydrated from the
	 * consuming project's config at packaging time.
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	FString SupabaseAnonKey;

	/**
	 * The mantle.place hosted native-login URL the system browser is sent to — the OAuth
	 * authorization endpoint. PKCE + redirect params are appended at runtime.
	 * Public route, compiled in; override via config to point at a non-production stack.
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Auth")
	FString WebLoginUrl = TEXT("https://mantle.place/auth/native");

	/**
	 * Endpoint for the PKCE code→token exchange. Public route, compiled in. When explicitly
	 * configured empty, falls back to Supabase-direct
	 * ({PlatformApiBaseUrl}/auth/v1/token?grant_type=pkce).
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	FString TokenEndpointUrl = TEXT("https://mantle.place/api/v1/auth/native/token");

	/**
	 * Explicit loopback ports for the redirect callback server, tried in order.
	 *
	 * LEAVE EMPTY (the default). The operating system then picks a free port each session, which is
	 * the only setting that survives Windows' Hyper-V/WinNAT reserved ranges — those move across
	 * reboots, a bind into one is refused although nothing is listening, and it is reported as if
	 * the port were in use. It is also what lets Revit and this editor, or two editors, sign in at
	 * the same time without competing for the same numbers.
	 *
	 * Set ports only when something outside this plugin needs a fixed redirect_uri. Each must then
	 * be allow-listed (as http://127.0.0.1:<port>/<callback>) by whatever validates the redirect,
	 * and each must fall outside every range that
	 * 'netsh interface ipv4 show excludedportrange protocol=tcp' reports.
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	TArray<int32> LoopbackPorts;

	/** Path component of the loopback redirect URI. */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	FString LoopbackCallbackPath = TEXT("/callback");

	/** Seconds to wait for the browser round-trip before a sign-in times out. */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	int32 SignInTimeoutSeconds = 300;

	/** When false (default), legacy password SignIn() is disabled in favor of SignInWithBrowser(). */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	bool bAllowPasswordGrant = false;

	/**
	 * Begin a system-browser sign-in (OAuth 2.0 Authorization Code Flow + PKCE, RFC 8252).
	 * Opens the default browser to WebLoginUrl, captures the redirect on a 127.0.0.1 loopback
	 * server, and exchanges the code for tokens. Result is delivered via OnSignInResult.
	 * This is the preferred sign-in path; the app never handles the user's password.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void SignInWithBrowser();

	/** Abort an in-flight browser sign-in (tears down the loopback server). Fires OnSignInResult(false). */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void CancelSignIn();

	/**
	 * Restore a prior session at startup using the securely stored refresh token (if any).
	 * Mints a fresh access token. Result is delivered via OnTokenRefreshed.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void TryRestoreSession();

	/**
	 * Begin a legacy email/password sign-in (direct password grant). Disabled unless
	 * bAllowPasswordGrant is true — prefer SignInWithBrowser. Result via OnSignInResult.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void SignIn(const FString& Email, const FString& Password);

	/** Clear the cached session locally. Always succeeds; fires OnSignOutComplete. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void SignOut();

	/** Exchange the cached refresh token for a fresh access token. Result via OnTokenRefreshed. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	void RefreshToken();

	/** Current auth state. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Auth")
	EMantlePlaceAuthState GetAuthState() const { return AuthState; }

	/** True only when authenticated with a non-expired access token. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Auth")
	bool IsAuthenticated() const;

	/** The current access token (JWT), or empty if not authenticated. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Auth")
	FString GetAccessToken() const { return Tokens.AccessToken; }

	/** Implemented by the Blueprint child: sign-in finished (success/failure + message). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Auth")
	void OnSignInResult(bool bSuccess, const FString& Message);

	/** Implemented by the Blueprint child: local sign-out completed. */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Auth")
	void OnSignOutComplete();

	/** Implemented by the Blueprint child: token refresh finished. */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Auth")
	void OnTokenRefreshed(bool bSuccess);

	/** Implemented by the Blueprint child: auth state changed. */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Auth")
	void OnAuthStateChanged(EMantlePlaceAuthState NewState);

	/** Native auth-state broadcast for C++/Slate observers (see the delegate note above). */
	FMantlePlaceOnAuthStateChangedNative OnAuthStateChangedNative;

	//~ Begin UObject interface
	virtual void BeginDestroy() override;
	//~ End UObject interface

private:
	/** Which auth grant a given HTTP exchange represents (selects the completion behavior). */
	enum class ERequestKind : uint8 { SignIn, Refresh, Restore, PkceExchange };

	/** Build, configure, and send a POST auth request; routes completion to HandleAuthResponse. */
	void SendAuthRequest(const FString& Url, const FString& Body, ERequestKind Kind);

	/** HTTP completion handler (game thread). Parses the response and fires the relevant event. */
	void HandleAuthResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
		bool bConnectedSuccessfully, ERequestKind Kind);

	/** Apply a failed-outcome state transition and fire the relevant event. */
	void HandleAuthFailure(const FString& Message, ERequestKind Kind);

	/** Unbind + cancel + reset any in-flight request. */
	void CancelActiveRequest();

	/** Single chokepoint for state changes: early-outs if unchanged, then fires OnAuthStateChanged. */
	void SetAuthState(EMantlePlaceAuthState NewState);

	//~ ----- Browser (PKCE) sign-in helpers -----

	/** Start the 127.0.0.1 loopback callback server on the first available LoopbackPort. */
	bool StartLoopbackServer(FString& OutError);

	/** Unbind the callback route and release the loopback router (idempotent). */
	void StopLoopbackServer();

	/**
	 * Ask the OS for a free loopback port by binding a throwaway socket to port 0 and reading back
	 * what it assigned. FHttpListener asserts on port 0, so the HTTP server cannot be asked directly.
	 */
	bool ProposeEphemeralLoopbackPort(int32& OutPort) const;

	/** Begin the PKCE code→token exchange using AuthCode + the pending code_verifier. */
	void BeginPkceTokenExchange(const FString& AuthCode);

	/**
	 * Tear down and fail an in-flight browser sign-in.
	 * bUserAborted selects the Cancel transition (→ Unauthenticated) vs SignInFailed (→ Failed).
	 */
	void AbortBrowserSignIn(const FString& Message, bool bUserAborted);

	/** Fill OutBytes with Count cryptographically-secure random bytes (BCryptGenRandom on Windows; /dev/urandom on POSIX). */
	bool GenerateRandomBytes(int32 Count, TArray<uint8>& OutBytes) const;

	/** Lazily construct the platform secret store. */
	void EnsureSecretStore();

	/** Persist the current refresh token to the secret store (no-op if empty / store unavailable). */
	void PersistRefreshToken();

	/** Arm / disarm / handle the sign-in timeout ticker. */
	void StartSignInTimeout();
	void StopSignInTimeout();
	bool OnSignInTimeout(float DeltaTime);

	/** The in-flight request (held for control/dedupe; the HTTP module also retains it). */
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> ActiveRequest;

	/** In-memory session tokens. The access token lives only here; the refresh token is additionally
	 *  persisted (encrypted) via SecretStore so a session can be restored on the next launch. */
	FMantlePlaceAuthTokens Tokens;

	/** Absolute UTC expiry of the cached access token (stamped when a response lands). */
	FDateTime ExpiresAtUtc = FDateTime(0);

	/** Current state machine position. */
	EMantlePlaceAuthState AuthState = EMantlePlaceAuthState::Unauthenticated;

	//~ Browser sign-in transient state.
	TSharedPtr<IHttpRouter> LoopbackRouter;
	TSharedPtr<const FHttpRouteHandleInternal> CallbackRouteHandle;
	int32 BoundLoopbackPort = 0;

	/**
	 * The port this process bound once and reuses for every later sign-in.
	 *
	 * FHttpServerModule keeps every listener it creates in a port-keyed map and frees entries only
	 * at module shutdown, so each DISTINCT port bound costs a listening socket for the rest of the
	 * session. Without this, an OS-assigned port per sign-in would leak one socket per sign-in.
	 * Re-asking for the same port is free — the module returns the live listener's router.
	 */
	int32 SessionLoopbackPort = 0;

	/** How many OS-assigned ports to try before giving up. Each attempt proposes a fresh one. */
	static constexpr int32 EphemeralLoopbackAttempts = 5;
	FString PendingCodeVerifier;
	FString PendingState;
	bool bSignInCallbackConsumed = false;
	FTSTicker::FDelegateHandle SignInTimeoutTicker;

	/** Encrypted at-rest store for the refresh token (DPAPI on Windows). */
	TSharedPtr<IMantlePlaceSecretStore> SecretStore;
};
