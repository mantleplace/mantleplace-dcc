// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceAuthSystemBase.h"

#include "MantlePlaceAuthLogic.h"
#include "MantlePlaceSecretStore.h"

#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"

#include "HttpServerModule.h"
#include "IHttpRouter.h"
#include "HttpServerRequest.h"
#include "HttpServerResponse.h"
#include "HttpPath.h"
#include "HttpRequestHandler.h"
#include "HttpResultCallback.h"
#include "HttpRouteHandle.h"

// The loopback callback port is probed on a plain socket before the HTTP server is asked to bind
// it: FHttpListener asserts on port 0, so there is no way to ask IT for an OS-assigned port.
#include "SocketSubsystem.h"
#include "Sockets.h"
#include "IPAddress.h"

#include "HAL/PlatformProcess.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "Containers/Ticker.h"

// CSPRNG for the PKCE code_verifier + state. Windows uses CNG (BCryptGenRandom); other desktop
// platforms read /dev/urandom. (We deliberately avoid OpenSSL here: its <openssl/...> headers
// redefine the type `UI`, which collides with a `namespace UI` in this translation unit.)
#if PLATFORM_WINDOWS
#include "Windows/AllowWindowsPlatformTypes.h"
#include <bcrypt.h>
#include "Windows/HideWindowsPlatformTypes.h"
#endif

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceAuth, Log, All);

namespace
{
	/** Secret-store key under which the refresh token is persisted. */
	const FString GRefreshTokenKey(TEXT("refresh_token"));
}

void UMantlePlaceAuthSystemBase::SignInWithBrowser()
{
	if (AuthState == EMantlePlaceAuthState::Authenticating || AuthState == EMantlePlaceAuthState::Refreshing)
	{
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("SignInWithBrowser ignored: an auth request is already in flight."));
		return;
	}

	if (WebLoginUrl.IsEmpty())
	{
		const FString Msg = TEXT("Browser sign-in is misconfigured: set WebLoginUrl (the mantle.place native-login URL) "
			"in DefaultGame.ini [/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase] or the BP child's class defaults.");
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("%s"), *Msg);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, Msg);
		return;
	}

	EnsureSecretStore();

	// Generate the PKCE verifier and a CSRF state from a CSPRNG (32 bytes each).
	TArray<uint8> VerifierBytes;
	TArray<uint8> StateBytes;
	if (!GenerateRandomBytes(32, VerifierBytes) || !GenerateRandomBytes(32, StateBytes))
	{
		const FString Msg = TEXT("Could not generate secure random data for sign-in.");
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("%s"), *Msg);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, Msg);
		return;
	}

	PendingCodeVerifier = FMantlePlaceAuthLogic::MakeCodeVerifier(VerifierBytes);
	PendingState = FMantlePlaceAuthLogic::MakeCodeVerifier(StateBytes);
	bSignInCallbackConsumed = false;
	const FString CodeChallenge = FMantlePlaceAuthLogic::MakeCodeChallengeS256(PendingCodeVerifier);

	// Stand up the loopback redirect server before opening the browser.
	FString ServerError;
	if (!StartLoopbackServer(ServerError))
	{
		PendingCodeVerifier.Reset();
		PendingState.Reset();
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("%s"), *ServerError);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, ServerError);
		return;
	}

	const FString RedirectUri = FMantlePlaceAuthLogic::BuildLoopbackRedirectUri(BoundLoopbackPort, LoopbackCallbackPath);
	const FString AuthorizeUrl = FMantlePlaceAuthLogic::BuildAuthorizeUrl(WebLoginUrl, RedirectUri, CodeChallenge, PendingState);

	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginSignIn));
	StartSignInTimeout();

	UE_LOG(LogMantlePlaceAuth, Log, TEXT("Opening system browser for sign-in (loopback redirect %s)."), *RedirectUri);
	FPlatformProcess::LaunchURL(*AuthorizeUrl, nullptr, nullptr);
}

void UMantlePlaceAuthSystemBase::CancelSignIn()
{
	if (AuthState != EMantlePlaceAuthState::Authenticating)
	{
		return;
	}
	AbortBrowserSignIn(TEXT("Sign-in cancelled."), /*bUserAborted=*/true);
}

void UMantlePlaceAuthSystemBase::TryRestoreSession()
{
	if (AuthState == EMantlePlaceAuthState::Authenticating || AuthState == EMantlePlaceAuthState::Refreshing)
	{
		return;
	}

	EnsureSecretStore();

	FString StoredRefresh;
	if (!SecretStore.IsValid() || !SecretStore->Load(GRefreshTokenKey, StoredRefresh) || StoredRefresh.IsEmpty())
	{
		// No stored session to restore — not an error.
		OnTokenRefreshed(false);
		return;
	}

	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(PlatformApiBaseUrl) || SupabaseAnonKey.IsEmpty())
	{
		UE_LOG(LogMantlePlaceAuth, Error,
			TEXT("TryRestoreSession: auth is misconfigured (PlatformApiBaseUrl='%s')."), *PlatformApiBaseUrl);
		OnTokenRefreshed(false);
		return;
	}

	// The stored token is a Supabase refresh token regardless of how sign-in was performed, so a
	// cold restore exchanges it directly against Supabase's refresh-token grant.
	Tokens.RefreshToken = StoredRefresh;
	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginRestore));

	const FString Url = FMantlePlaceAuthLogic::BuildRefreshGrantUrl(PlatformApiBaseUrl);
	const FString Body = FMantlePlaceAuthLogic::BuildRefreshGrantBody(StoredRefresh);
	SendAuthRequest(Url, Body, ERequestKind::Restore);
}

void UMantlePlaceAuthSystemBase::SignIn(const FString& Email, const FString& Password)
{
	if (!bAllowPasswordGrant)
	{
		const FString Msg = TEXT("Password sign-in is disabled. Use SignInWithBrowser() (OAuth 2.0 + PKCE).");
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("%s"), *Msg);
		OnSignInResult(false, Msg);
		return;
	}

	if (AuthState == EMantlePlaceAuthState::Authenticating || AuthState == EMantlePlaceAuthState::Refreshing)
	{
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("SignIn ignored: an auth request is already in flight."));
		return;
	}

	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(PlatformApiBaseUrl) || SupabaseAnonKey.IsEmpty())
	{
		// Fail fast and loud on a misconfigured endpoint. A scheme-only value (e.g. a half-typed
		// "https:") would otherwise build the hostless URL "https:/auth/v1/token" and surface the
		// misleading "Network error: no response from the platform" after a DNS timeout.
		const FString Msg = FString::Printf(
			TEXT("Auth is misconfigured: PlatformApiBaseUrl ('%s') must be a full http(s) URL with a host "
			     "(e.g. https://<ref>.supabase.co) and SupabaseAnonKey must be set. Set them in DefaultGame.ini "
			     "[/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase] or the BP child's class defaults."),
			*PlatformApiBaseUrl);
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("%s"), *Msg);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, Msg);
		return;
	}

	if (Email.IsEmpty() || Password.IsEmpty())
	{
		const FString Msg = TEXT("Email and password are required.");
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("%s"), *Msg);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, Msg);
		return;
	}

	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginSignIn));

	const FString Url = FMantlePlaceAuthLogic::BuildPasswordGrantUrl(PlatformApiBaseUrl);
	const FString Body = FMantlePlaceAuthLogic::BuildPasswordGrantBody(Email, Password);
	SendAuthRequest(Url, Body, ERequestKind::SignIn);
}

void UMantlePlaceAuthSystemBase::SignOut()
{
	CancelActiveRequest();
	StopLoopbackServer();
	StopSignInTimeout();
	PendingCodeVerifier.Reset();
	PendingState.Reset();
	bSignInCallbackConsumed = false;

	Tokens.Reset();
	ExpiresAtUtc = FDateTime(0);

	EnsureSecretStore();
	if (SecretStore.IsValid())
	{
		SecretStore->Clear(GRefreshTokenKey);
	}

	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignOut));
	OnSignOutComplete();
}

void UMantlePlaceAuthSystemBase::RefreshToken()
{
	if (AuthState == EMantlePlaceAuthState::Authenticating || AuthState == EMantlePlaceAuthState::Refreshing)
	{
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("RefreshToken ignored: an auth request is already in flight."));
		return;
	}

	if (AuthState != EMantlePlaceAuthState::Authenticated || Tokens.RefreshToken.IsEmpty())
	{
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("RefreshToken ignored: no active session to refresh."));
		OnTokenRefreshed(false);
		return;
	}

	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(PlatformApiBaseUrl) || SupabaseAnonKey.IsEmpty())
	{
		UE_LOG(LogMantlePlaceAuth, Error,
			TEXT("RefreshToken: auth is misconfigured (PlatformApiBaseUrl='%s')."), *PlatformApiBaseUrl);
		OnTokenRefreshed(false);
		return;
	}

	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginRefresh));

	const FString Url = FMantlePlaceAuthLogic::BuildRefreshGrantUrl(PlatformApiBaseUrl);
	const FString Body = FMantlePlaceAuthLogic::BuildRefreshGrantBody(Tokens.RefreshToken);
	SendAuthRequest(Url, Body, ERequestKind::Refresh);
}

bool UMantlePlaceAuthSystemBase::IsAuthenticated() const
{
	return AuthState == EMantlePlaceAuthState::Authenticated
		&& Tokens.IsValid()
		&& !FMantlePlaceAuthLogic::IsExpired(FDateTime::UtcNow(), ExpiresAtUtc);
}

void UMantlePlaceAuthSystemBase::BeginDestroy()
{
	CancelActiveRequest();
	StopLoopbackServer();
	StopSignInTimeout();
	Super::BeginDestroy();
}

void UMantlePlaceAuthSystemBase::SendAuthRequest(const FString& Url, const FString& Body, ERequestKind Kind)
{
	// Defensively drop any stale request before launching a new one.
	CancelActiveRequest();

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("POST"));
	ActiveRequest->SetURL(Url);
	ActiveRequest->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	// The anon key authenticates Supabase-direct calls (password/refresh/restore, and the PKCE
	// fallback). It is unnecessary — and harmlessly ignored — for the web-broker token endpoint.
	if (!SupabaseAnonKey.IsEmpty())
	{
		ActiveRequest->SetHeader(TEXT("apikey"), SupabaseAnonKey);
		ActiveRequest->SetHeader(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *SupabaseAnonKey));
	}
	ActiveRequest->SetContentAsString(Body);

	// Capture a weak pointer, never raw `this`: the request completes across frames and the
	// owning UObject may be GC'd mid-flight (PIE end, world teardown, BP actor destroyed).
	TWeakObjectPtr<UMantlePlaceAuthSystemBase> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis, Kind](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceAuthSystemBase* Self = WeakThis.Get())
			{
				Self->HandleAuthResponse(Request, Response, bConnectedSuccessfully, Kind);
			}
			// else: owner was GC'd — nothing to do, no use-after-free.
		});

	if (!ActiveRequest->ProcessRequest())
	{
		HandleAuthFailure(TEXT("Failed to start the HTTP request."), Kind);
	}
}

void UMantlePlaceAuthSystemBase::HandleAuthResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully, ERequestKind Kind)
{
	// This request is finished; release our handle (the lambda is no longer needed).
	ActiveRequest.Reset();

	// Axis 1: transport failure (no response reached us).
	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		HandleAuthFailure(TEXT("Network error: no response from the platform."), Kind);
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	// Axis 2: HTTP error code — surface the error message if present.
	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FString Error;
		if (!FMantlePlaceAuthLogic::ParseErrorResponse(Content, Error))
		{
			Error = FString::Printf(TEXT("HTTP %d"), ResponseCode);
		}
		HandleAuthFailure(Error, Kind);
		return;
	}

	// 2xx — parse the token payload (identical GoTrue token shape for every grant).
	FMantlePlaceAuthTokens NewTokens;
	FString ParseError;
	if (!FMantlePlaceAuthLogic::ParseTokenResponse(Content, NewTokens, ParseError))
	{
		HandleAuthFailure(ParseError, Kind);
		return;
	}

	// Success — overwrite the token set, but keep the prior refresh_token when the response omits a
	// new one, and stamp absolute expiry from wall-clock now.
	const FString PriorRefreshToken = Tokens.RefreshToken;
	Tokens = MoveTemp(NewTokens);
	Tokens.RefreshToken = FMantlePlaceAuthLogic::ChooseRefreshToken(Tokens.RefreshToken, PriorRefreshToken);
	ExpiresAtUtc = FDateTime::UtcNow() + FTimespan::FromSeconds(Tokens.ExpiresInSeconds);

	switch (Kind)
	{
	case ERequestKind::SignIn:
	case ERequestKind::PkceExchange:
		if (Kind == ERequestKind::PkceExchange)
		{
			// The redirect has been consumed and the code exchanged — the loopback server is done.
			StopLoopbackServer();
			StopSignInTimeout();
			PendingCodeVerifier.Reset();
			PendingState.Reset();
			bSignInCallbackConsumed = false;
		}
		PersistRefreshToken();
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignInSucceeded));
		UE_LOG(LogMantlePlaceAuth, Log, TEXT("Sign-in succeeded."));
		OnSignInResult(true, TEXT("Sign-in succeeded."));
		break;

	case ERequestKind::Refresh:
	case ERequestKind::Restore:
		// Re-persist the (possibly rotated) refresh token so the stored copy never goes stale.
		PersistRefreshToken();
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::RefreshSucceeded));
		UE_LOG(LogMantlePlaceAuth, Log, TEXT("Token %s succeeded."),
			Kind == ERequestKind::Restore ? TEXT("restore") : TEXT("refresh"));
		OnTokenRefreshed(true);
		break;
	}
}

void UMantlePlaceAuthSystemBase::HandleAuthFailure(const FString& Message, ERequestKind Kind)
{
	switch (Kind)
	{
	case ERequestKind::SignIn:
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("Auth sign-in failed: %s"), *Message);
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignInFailed));
		OnSignInResult(false, Message);
		break;

	case ERequestKind::PkceExchange:
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("Auth browser sign-in failed: %s"), *Message);
		StopLoopbackServer();
		StopSignInTimeout();
		PendingCodeVerifier.Reset();
		PendingState.Reset();
		bSignInCallbackConsumed = false;
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignInFailed));
		OnSignInResult(false, Message);
		break;

	case ERequestKind::Refresh:
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("Auth refresh failed: %s"), *Message);
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::RefreshFailed));
		OnTokenRefreshed(false);
		break;

	case ERequestKind::Restore:
		// A cold restore failed: drop the (stale/unreachable) in-memory token. We deliberately keep
		// the persisted copy so a transient startup network blip doesn't force a re-login next launch;
		// a genuinely dead token is overwritten on the next successful browser sign-in.
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("Session restore failed: %s"), *Message);
		Tokens.Reset();
		ExpiresAtUtc = FDateTime(0);
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::RefreshFailed));
		OnTokenRefreshed(false);
		break;
	}
}

void UMantlePlaceAuthSystemBase::CancelActiveRequest()
{
	if (ActiveRequest.IsValid())
	{
		ActiveRequest->OnProcessRequestComplete().Unbind();
		if (ActiveRequest->GetStatus() == EHttpRequestStatus::Processing)
		{
			ActiveRequest->CancelRequest();
		}
		ActiveRequest.Reset();
	}
}

void UMantlePlaceAuthSystemBase::SetAuthState(EMantlePlaceAuthState NewState)
{
	if (AuthState == NewState)
	{
		return;
	}
	AuthState = NewState;
	OnAuthStateChanged(NewState);              // Blueprint child surface
	OnAuthStateChangedNative.Broadcast(NewState); // native observers (editor orchestrator/panel)
}

bool UMantlePlaceAuthSystemBase::StartLoopbackServer(FString& OutError)
{
	StopLoopbackServer();

	FHttpServerModule& ServerModule = FHttpServerModule::Get();

	// ORDER IS LOAD-BEARING: enable listeners BEFORE probing candidate ports.
	//
	// GetHttpRouter(Port, bFailOnBindFailure=true) only attempts a real socket bind when the
	// module-global "listeners enabled" flag is already set — and that flag is set by
	// StartAllListeners(). Called the other way round (probe first, start after), the first
	// sign-in of every process binds nothing, GetHttpRouter returns a valid router for whatever
	// port we asked about, and the loop below "succeeds" on candidate #1 no matter what. The
	// bind then fails inside StartAllListeners(), too late to fall back, and the browser opens
	// on a dead port with no in-app failure at all. Starting first makes each probe an honest
	// bind-or-null, which is exactly what the loop already assumes.
	ServerModule.StartAllListeners();

	FString CallbackPath = LoopbackCallbackPath;
	if (!CallbackPath.StartsWith(TEXT("/")))
	{
		CallbackPath = TEXT("/") + CallbackPath;
	}

	TWeakObjectPtr<UMantlePlaceAuthSystemBase> WeakThis(this);

	auto TryAcquire = [this, &ServerModule, &CallbackPath, WeakThis](int32 Port) -> bool
	{
		TSharedPtr<IHttpRouter> Router = ServerModule.GetHttpRouter(static_cast<uint32>(Port), /*bFailOnBindFailure=*/true);
		if (!Router.IsValid())
		{
			return false; // Port reserved or in use — try the next candidate.
		}

		FHttpRouteHandle Handle = Router->BindRoute(
			FHttpPath(CallbackPath),
			EHttpServerRequestVerbs::VERB_GET,
			FHttpRequestHandler::CreateLambda(
				[WeakThis](const FHttpServerRequest& ServerRequest, const FHttpResultCallback& OnComplete) -> bool
				{
					UMantlePlaceAuthSystemBase* Self = WeakThis.Get();
					if (!Self)
					{
						// Owner gone — answer the browser so the tab doesn't hang.
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserErrorHtml(TEXT("The application is no longer running.")),
							TEXT("text/html")));
						return true;
					}

					// Ignore a duplicate / late redirect once the first has been consumed.
					if (Self->bSignInCallbackConsumed)
					{
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserSuccessHtml(), TEXT("text/html")));
						return true;
					}
					Self->bSignInCallbackConsumed = true;

					const FString* CodePtr = ServerRequest.QueryParams.Find(TEXT("code"));
					const FString* StatePtr = ServerRequest.QueryParams.Find(TEXT("state"));
					const FString* ErrorPtr = ServerRequest.QueryParams.Find(TEXT("error"));
					const FString* ErrorDescPtr = ServerRequest.QueryParams.Find(TEXT("error_description"));

					FString FailMessage;
					if (ErrorPtr != nullptr)
					{
						FailMessage = (ErrorDescPtr != nullptr && !ErrorDescPtr->IsEmpty()) ? *ErrorDescPtr : *ErrorPtr;
					}
					else if (!FMantlePlaceAuthLogic::IsStateValid(Self->PendingState, StatePtr ? *StatePtr : FString()))
					{
						FailMessage = TEXT("Sign-in state mismatch (possible CSRF). Please try again.");
					}
					else if (CodePtr == nullptr || CodePtr->IsEmpty())
					{
						FailMessage = TEXT("No authorization code was returned.");
					}

					if (FailMessage.IsEmpty())
					{
						// Success: answer the browser first, then exchange the code for tokens. The
						// loopback server is torn down when that exchange completes (a later tick) —
						// never synchronously from inside its own route handler.
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserSuccessHtml(), TEXT("text/html")));
						Self->BeginPkceTokenExchange(*CodePtr);
					}
					else
					{
						// Failure: answer the browser, then defer teardown to the next tick so we don't
						// unbind this route while it is still executing.
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserErrorHtml(FailMessage), TEXT("text/html")));
						const FString DeferredMessage = FailMessage;
						FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateWeakLambda(Self,
							[WeakThis, DeferredMessage](float) -> bool
							{
								if (UMantlePlaceAuthSystemBase* Late = WeakThis.Get())
								{
									Late->AbortBrowserSignIn(DeferredMessage, /*bUserAborted=*/false);
								}
								return false;
							}), 0.0f);
					}
					return true;
				}));

		if (!Handle.IsValid())
		{
			return false; // Couldn't bind the route here — try the next port.
		}

		LoopbackRouter = Router;
		CallbackRouteHandle = Handle;
		return true;
	};

	const FMantlePlaceAuthLogic::ELoopbackPortMode Mode =
	    FMantlePlaceAuthLogic::ResolveLoopbackPortMode(LoopbackPorts);

	int32 SelectedPort = 0;
	bool bAcquired = false;

	// REUSE THIS PROCESS'S PORT IF WE ALREADY HAVE ONE, and note that this is not an optimisation.
	//
	// FHttpServerModule keeps every listener it ever created in a port-keyed map and removes an
	// entry only at module shutdown; StopAllListeners() flips a flag, it does not free anything.
	// So each DISTINCT port this process successfully binds costs a listening socket for the rest
	// of the session. A fresh ephemeral port per sign-in would therefore leak one socket per
	// sign-in — strictly worse than the fixed range it replaces. Asking for the same port again is
	// free: GetHttpRouter finds the live listener and hands back its existing router without
	// binding anything. One socket per editor process is the trade StopLoopbackServer already
	// makes, and this keeps it.
	if (SessionLoopbackPort != 0 && TryAcquire(SessionLoopbackPort))
	{
		SelectedPort = SessionLoopbackPort;
		bAcquired = true;
	}
	else if (Mode == FMantlePlaceAuthLogic::ELoopbackPortMode::DeclaredList)
	{
		bAcquired = FMantlePlaceAuthLogic::SelectLoopbackPort(LoopbackPorts, TryAcquire, SelectedPort);
	}
	else
	{
		bAcquired = FMantlePlaceAuthLogic::AcquireEphemeralLoopbackPort(
		    [this](int32& OutProposed)
		    { return ProposeEphemeralLoopbackPort(OutProposed); },
		    TryAcquire,
		    EphemeralLoopbackAttempts,
		    SelectedPort);
	}

	if (!bAcquired)
	{
		if (Mode == FMantlePlaceAuthLogic::ELoopbackPortMode::DeclaredList)
		{
			// Name the ports actually tried. "In use" would be a guess — and usually the wrong one:
			// the common cause on Windows is a reserved range, where nothing is listening at all.
			OutError = FString::Printf(
			    TEXT("Could not start the local sign-in callback server: none of the loopback ports "
			         "configured in LoopbackPorts could be bound (tried %s)."),
			    *FString::JoinBy(LoopbackPorts, TEXT(", "), [](int32 Port)
			                     { return FString::FromInt(Port); }));
#if PLATFORM_WINDOWS
			OutError += TEXT(" On Windows a bind is also refused for ports inside a Hyper-V/WinNAT reserved "
			                 "range, even with nothing listening. List the ranges with "
			                 "'netsh interface ipv4 show excludedportrange protocol=tcp' and pick ports outside them — or "
			                 "clear LoopbackPorts entirely and let the operating system choose.");
#endif
		}
		else
		{
			// No port list to name, and the netsh hint would mislead here: the OS does not hand out
			// a port from its own reserved ranges, so a reserved range is not what went wrong.
			OutError = FString::Printf(
			    TEXT("Could not start the local sign-in callback server: the operating system offered "
			         "%d ports and none could be bound. This is normally a firewall or endpoint-security "
			         "product blocking local HTTP listeners — it is not another Mantle Place session, "
			         "which would have its own port. See the LogHttpServerModule output for the "
			         "underlying error."),
			    EphemeralLoopbackAttempts);
		}
		return false;
	}

	BoundLoopbackPort = SelectedPort;
	SessionLoopbackPort = SelectedPort;

	UE_LOG(LogTemp, Log, TEXT("Mantle Place: sign-in callback listening on http://127.0.0.1:%d%s"),
	       SelectedPort, *CallbackPath);

	return true;
}

bool UMantlePlaceAuthSystemBase::ProposeEphemeralLoopbackPort(int32& OutPort) const
{
	ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	if (Sockets == nullptr)
	{
		return false;
	}

	// The literal loopback, matching FHttpServerListenerConfig's own default bind address. Probing
	// a different interface than the listener will use would make the answer meaningless.
	TSharedRef<FInternetAddr> Addr = Sockets->CreateInternetAddr();
	Addr->SetLoopbackAddress();
	Addr->SetPort(0);

	// Same two-argument form FHttpListener::StartListening uses, so the probe allocates the same
	// kind of socket the real listener will.
	FUniqueSocket Probe = Sockets->CreateUniqueSocket(NAME_Stream, TEXT("MantlePlaceLoopbackProbe"));
	if (!Probe.IsValid())
	{
		return false;
	}

	// Explicit, not inherited: a project that turns on HTTPServer.DefaultReuseAddressAndPortEnabled
	// would otherwise let this probe share a port with someone else, and it would be reporting a
	// port it does not hold.
	Probe->SetReuseAddr(false);

	if (!Probe->Bind(*Addr))
	{
		return false;
	}

	// Listening is what actually commits the port, and it is what FHttpListener will do next.
	if (!Probe->Listen(1))
	{
		return false;
	}

	OutPort = Probe->GetPortNo();
	return OutPort > 0;
}

void UMantlePlaceAuthSystemBase::StopLoopbackServer()
{
	if (LoopbackRouter.IsValid() && CallbackRouteHandle.IsValid())
	{
		LoopbackRouter->UnbindRoute(CallbackRouteHandle);
	}
	CallbackRouteHandle.Reset();
	LoopbackRouter.Reset();
	BoundLoopbackPort = 0;
}

void UMantlePlaceAuthSystemBase::BeginPkceTokenExchange(const FString& AuthCode)
{
	// Prefer the configured web token endpoint; fall back to Supabase-direct PKCE exchange.
	FString Url = TokenEndpointUrl;
	if (Url.IsEmpty())
	{
		Url = FMantlePlaceAuthLogic::BuildPkceTokenUrl(PlatformApiBaseUrl);
	}
	const FString Body = FMantlePlaceAuthLogic::BuildPkceTokenBody(AuthCode, PendingCodeVerifier);
	SendAuthRequest(Url, Body, ERequestKind::PkceExchange);
}

void UMantlePlaceAuthSystemBase::AbortBrowserSignIn(const FString& Message, bool bUserAborted)
{
	StopLoopbackServer();
	StopSignInTimeout();
	CancelActiveRequest();
	PendingCodeVerifier.Reset();
	PendingState.Reset();
	bSignInCallbackConsumed = false;

	// A user cancel / timeout returns to a clean Unauthenticated state; a genuine failure latches Failed.
	const EMantlePlaceAuthEvent Event = bUserAborted
		? EMantlePlaceAuthEvent::Cancel
		: EMantlePlaceAuthEvent::SignInFailed;
	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, Event));
	OnSignInResult(false, Message);
}

bool UMantlePlaceAuthSystemBase::GenerateRandomBytes(int32 Count, TArray<uint8>& OutBytes) const
{
	if (Count <= 0)
	{
		return false;
	}
	OutBytes.SetNumUninitialized(Count);

#if PLATFORM_WINDOWS
	const NTSTATUS Status = BCryptGenRandom(nullptr, OutBytes.GetData(), static_cast<ULONG>(Count),
		BCRYPT_USE_SYSTEM_PREFERRED_RNG);
	return BCRYPT_SUCCESS(Status);
#else
	// Portable CSPRNG: read raw bytes from /dev/urandom.
	TUniquePtr<IFileHandle> Handle(IPlatformFile::GetPlatformPhysical().OpenRead(TEXT("/dev/urandom")));
	return Handle.IsValid() && Handle->Read(OutBytes.GetData(), Count);
#endif
}

void UMantlePlaceAuthSystemBase::EnsureSecretStore()
{
	if (!SecretStore.IsValid())
	{
		SecretStore = MakeShareable(IMantlePlaceSecretStore::Create().Release());
	}
}

void UMantlePlaceAuthSystemBase::PersistRefreshToken()
{
	EnsureSecretStore();
	if (SecretStore.IsValid() && !Tokens.RefreshToken.IsEmpty())
	{
		SecretStore->Save(GRefreshTokenKey, Tokens.RefreshToken);
	}
}

void UMantlePlaceAuthSystemBase::StartSignInTimeout()
{
	StopSignInTimeout();
	const float Delay = static_cast<float>(FMath::Max(1, SignInTimeoutSeconds));
	SignInTimeoutTicker = FTSTicker::GetCoreTicker().AddTicker(
		FTickerDelegate::CreateUObject(this, &UMantlePlaceAuthSystemBase::OnSignInTimeout), Delay);
}

void UMantlePlaceAuthSystemBase::StopSignInTimeout()
{
	if (SignInTimeoutTicker.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(SignInTimeoutTicker);
		SignInTimeoutTicker.Reset();
	}
}

bool UMantlePlaceAuthSystemBase::OnSignInTimeout(float /*DeltaTime*/)
{
	SignInTimeoutTicker.Reset(); // Returning false unregisters us; drop the handle too.
	if (AuthState == EMantlePlaceAuthState::Authenticating)
	{
		AbortBrowserSignIn(TEXT("Sign-in timed out. Please try again."), /*bUserAborted=*/true);
	}
	return false;
}
