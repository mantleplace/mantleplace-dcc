// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "Containers/Ticker.h"
#include "MantlePlaceAuthTypes.h"
#include "MantlePlaceClientLogic.h"
#include "MantlePlaceAuthSystemBase.generated.h"

class IHttpRequest;
class IHttpResponse;
class IHttpRouter;
struct FHttpRouteHandleInternal;
class IMantlePlaceSecretStore;

DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnAuthStateChangedNative, EMantlePlaceAuthState);

DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnTokenRefreshedNative, bool);

/**
 * C++ base for the Mantle Place auth system.
 *
 * Owns the auth LOGIC: OAuth 2.0 Authorization Code + PKCE through the system browser, the token
 * set in memory, and the auth state. The deterministic core (URL/body construction, response
 * parsing, expiry arithmetic, the rejection classifier, the state machine) lives in the
 * headless-testable FMantlePlaceAuthLogic; this class is the thin impure shim that issues the
 * requests and fires the events.
 *
 * Every route this class talks to is a public mantle.place route, compiled in. There is no
 * packaging-time secret and no identity-provider-direct path: a plugin that cannot sign in, stay
 * signed in, or sign out without a config file is a plugin that cannot do those things, and for
 * anyone who had not packaged the plugin that is exactly what used to happen.
 *
 * The surface is native Slate (SMantlePlaceVaultPanel). There is no Blueprint child: the editor's
 * one session is owned by UMantlePlaceAuthSubsystem, which has a startup moment a Blueprint graph
 * cannot offer. See docs/platform-auth-contract.md, at the repository root, for what mantle.place
 * must serve — it is cross-host, and the Revit plugin reads the same rejection codes.
 */
UCLASS(Blueprintable, config = Game)
class MANTLEPLACERUNTIME_API UMantlePlaceAuthSystemBase : public UObject
{
	GENERATED_BODY()

public:
	/**
	 * The mantle.place hosted native-login URL the system browser is sent to — the OAuth
	 * authorization endpoint. PKCE + redirect params are appended at runtime.
	 * Public route, compiled in; override via config to point at a non-production stack.
	 *
	 * `host` is how the sign-in page knows which editor is asking, so its heading, credential prompt
	 * and tab title can name Unreal instead of saying "the application". The value is the bundle
	 * manifest's hosts.<hostId> id — one vocabulary across the manifest and the sign-in surface.
	 *
	 * It is NOT a credential. On the platform side it is display-only: read off an untrusted query,
	 * allowlisted so an unrecognised value renders nothing, never validated against a client registry
	 * and never able to fail or alter a request. Do not start signing it or treating it as a client
	 * id.
	 *
	 * It rides the configured base rather than the appended query, which is why the pinned authorize
	 * parameter order is unaffected — BuildAuthorizeUrl already picks '&' over '?' when the base
	 * carries a query, so the five OAuth parameters follow in the same order they always did.
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Auth")
	FString WebLoginUrl = TEXT("https://mantle.place/auth/native?host=unreal");

	/**
	 * Endpoint for the PKCE code→token exchange. Public route, compiled in.
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	FString TokenEndpointUrl = TEXT("https://mantle.place/api/v1/auth/native/token");

	/**
	 * Endpoint that exchanges a stored refresh token for a new access token. Public route,
	 * compiled in, and the reason a curator with no packaging-time configuration can stay signed in.
	 *
	 * Refresh and restore used to be identity-provider-direct, which needed values hydrated at
	 * packaging time and absent from a plain clone: sign-in worked config-free and restore did not,
	 * on the machine of everyone who had not packaged the plugin. The Revit host has the same route
	 * for the same reason.
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	FString RefreshEndpointUrl = TEXT("https://mantle.place/api/v1/auth/native/refresh");

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

	/**
	 * When false, SignInWithBrowser() prepares the full flow — loopback server,
	 * PKCE, timeout — but does NOT open the system browser; it logs the
	 * authorize URL instead, for an external agent to complete. The standard
	 * headless/kiosk/CI courtesy (same shape as `gcloud auth login --no-launch-browser`):
	 * environments where popping a browser window is wrong can still run the
	 * exact production auth path. Defaults to true — end users keep the
	 * one-click experience.
	 */
	UPROPERTY(EditDefaultsOnly, Config, Category = "Mantle Place|Auth")
	bool bLaunchBrowserForSignIn = true;

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

	FMantlePlaceOnAuthStateChangedNative OnAuthStateChangedNative;

	FMantlePlaceOnTokenRefreshedNative OnTokenRefreshedNative;

	FMantlePlaceOnClientUpdateRequiredNative OnClientUpdateRequiredNative;

	/**
	 * True while a refresh or restore is in flight.
	 *
	 * Callers use it to JOIN an existing renewal rather than starting a second one: two vault
	 * operations that both see an expired token must produce one refresh, not two, or the second
	 * presents a refresh token the first has already rotated away.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	bool IsRefreshInFlight() const { return AuthState == EMantlePlaceAuthState::Refreshing; }

	/**
	 * True when there is a refresh token to renew from, whether or not the access token is still
	 * good. Distinguishes "signed out" from "signed in but needs a new access token".
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	bool CanRenewSession() const { return !Tokens.RefreshToken.IsEmpty(); }

	/**
	 * Why the last auth attempt failed, in words fit to show a user. Empty when nothing has failed
	 * since the last success.
	 *
	 * A failed restore used to be invisible: the state went to Failed, the panel rendered Failed as
	 * a plain "Sign In" button, and the reason existed only in a log line nobody reads. A silent
	 * failure has no bug report attached to it, which is why this defect survived as long as it did.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Auth")
	const FString& GetLastAuthError() const { return LastAuthError; }

	virtual void BeginDestroy() override;

private:
	enum class ERequestKind : uint8 { SignIn, Refresh, Restore, PkceExchange };

	void SendAuthRequest(const FString& Url, const FString& Body, ERequestKind Kind);

	void HandleAuthResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
		bool bConnectedSuccessfully, ERequestKind Kind);

	void HandleAuthFailure(const FString& Message, ERequestKind Kind, bool bDefinitive);

	void NotifyTokenRefreshed(bool bSuccess);

	FString ReadFreshestRefreshToken();

	void ForgetStoredSession();

	void CancelActiveRequest();

	void SetAuthState(EMantlePlaceAuthState NewState);

	bool StartLoopbackServer(FString& OutError);

	void StopLoopbackServer();

	bool ProposeEphemeralLoopbackPort(int32& OutPort) const;

	void BeginPkceTokenExchange(const FString& AuthCode);

	void AbortBrowserSignIn(const FString& Message, bool bUserAborted);

	bool GenerateRandomBytes(int32 Count, TArray<uint8>& OutBytes) const;

	void EnsureSecretStore();

	void PersistRefreshToken();

	void StartSignInTimeout();
	void StopSignInTimeout();
	bool OnSignInTimeout(float DeltaTime);

	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> ActiveRequest;

	FMantlePlaceAuthTokens Tokens;

	FDateTime ExpiresAtUtc = FDateTime(0);

	EMantlePlaceAuthState AuthState = EMantlePlaceAuthState::Unauthenticated;

	TSharedPtr<IHttpRouter> LoopbackRouter;
	TSharedPtr<const FHttpRouteHandleInternal> CallbackRouteHandle;
	int32 BoundLoopbackPort = 0;

	int32 SessionLoopbackPort = 0;

	static constexpr int32 EphemeralLoopbackAttempts = 5;
	FString PendingCodeVerifier;
	FString PendingState;
	bool bSignInCallbackConsumed = false;
	FTSTicker::FDelegateHandle SignInTimeoutTicker;

	TSharedPtr<IMantlePlaceSecretStore> SecretStore;

	FString LastAuthError;

	FString PresentedRefreshToken;

	bool bRotationRetryUsed = false;
};
