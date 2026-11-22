// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceAuthSystemBase.h"

#include "MantlePlaceAuthLogic.h"
#include "MantlePlaceClientLogic.h"
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

#include "SocketSubsystem.h"
#include "Sockets.h"
#include "IPAddress.h"

#include "HAL/PlatformProcess.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "Containers/Ticker.h"

#if PLATFORM_WINDOWS
#include "Windows/AllowWindowsPlatformTypes.h"
#include <bcrypt.h>
#include "Windows/HideWindowsPlatformTypes.h"
#endif

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceAuth, Log, All);

namespace
{
	const FString GRefreshTokenKey(TEXT("refresh-token"));
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
		const FString Msg = TEXT("Browser sign-in is misconfigured: WebLoginUrl (the mantle.place native-login "
			"URL) has been cleared. It has a working default; nothing needs to set it.");
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("%s"), *Msg);
		SetAuthState(EMantlePlaceAuthState::Failed);
		OnSignInResult(false, Msg);
		return;
	}

	EnsureSecretStore();

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

	if (bLaunchBrowserForSignIn)
	{
		UE_LOG(LogMantlePlaceAuth, Log, TEXT("Opening system browser for sign-in (loopback redirect %s)."), *RedirectUri);
		FPlatformProcess::LaunchURL(*AuthorizeUrl, nullptr, nullptr);
	}
	else
	{
		UE_LOG(LogMantlePlaceAuth, Log, TEXT("Browser launch disabled; complete sign-in externally: %s"), *AuthorizeUrl);
	}
}

void UMantlePlaceAuthSystemBase::CancelSignIn()
{
	if (AuthState != EMantlePlaceAuthState::Authenticating)
	{
		return;
	}
	AbortBrowserSignIn(TEXT("Sign-in cancelled."), true);
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
		NotifyTokenRefreshed(false);
		return;
	}

	if (RefreshEndpointUrl.IsEmpty())
	{
		LastAuthError = TEXT("Mantle Place sign-in is not configured on this install.");
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("TryRestoreSession: %s"), *LastAuthError);
		NotifyTokenRefreshed(false);
		return;
	}

	Tokens.RefreshToken = StoredRefresh;
	PresentedRefreshToken = StoredRefresh;
	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginRestore));

	const FString Body = FMantlePlaceAuthLogic::BuildRefreshGrantBody(StoredRefresh);
	SendAuthRequest(RefreshEndpointUrl, Body, ERequestKind::Restore);
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

	ForgetStoredSession();
	LastAuthError.Reset();

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
		NotifyTokenRefreshed(false);
		return;
	}

	if (RefreshEndpointUrl.IsEmpty())
	{
		LastAuthError = TEXT("Mantle Place sign-in is not configured on this install.");
		UE_LOG(LogMantlePlaceAuth, Error, TEXT("RefreshToken: %s"), *LastAuthError);
		NotifyTokenRefreshed(false);
		return;
	}

	Tokens.RefreshToken = ReadFreshestRefreshToken();
	PresentedRefreshToken = Tokens.RefreshToken;

	SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::BeginRefresh));

	const FString Body = FMantlePlaceAuthLogic::BuildRefreshGrantBody(Tokens.RefreshToken);
	SendAuthRequest(RefreshEndpointUrl, Body, ERequestKind::Refresh);
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
	CancelActiveRequest();

	ActiveRequest = FMantlePlaceClientHttp::CreateRequest(TEXT("POST"), Url);
	ActiveRequest->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	ActiveRequest->SetContentAsString(Body);

	TWeakObjectPtr<UMantlePlaceAuthSystemBase> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis, Kind](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceAuthSystemBase* Self = WeakThis.Get())
			{
				Self->HandleAuthResponse(Request, Response, bConnectedSuccessfully, Kind);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		HandleAuthFailure(TEXT("Could not start the request to mantle.place."), Kind, false);
	}
}

void UMantlePlaceAuthSystemBase::HandleAuthResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully, ERequestKind Kind)
{
	ActiveRequest.Reset();

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		HandleAuthFailure(TEXT("Could not reach mantle.place."), Kind, false);
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	FMantlePlaceClientUpdate Update;
	if (FMantlePlaceClientLogic::ParseClientUpdateRequired(ResponseCode, Content, Update))
	{
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("The platform requires a newer plugin (sent %s, minimum %s)."),
			*Update.SentVersion, *Update.MinimumVersion);
		OnClientUpdateRequiredNative.Broadcast(Update);
		HandleAuthFailure(Update.Message, Kind, false);
		return;
	}

	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FString Error;
		if (!FMantlePlaceAuthLogic::ParseErrorResponse(Content, Error))
		{
			Error = FString::Printf(TEXT("mantle.place refused this request (HTTP %d)."), ResponseCode);
		}
		HandleAuthFailure(Error, Kind, FMantlePlaceAuthLogic::IsDefinitiveRejection(ResponseCode, Content));
		return;
	}

	FMantlePlaceAuthTokens NewTokens;
	FString ParseError;
	if (!FMantlePlaceAuthLogic::ParseTokenResponse(Content, NewTokens, ParseError))
	{
		HandleAuthFailure(ParseError, Kind, false);
		return;
	}

	LastAuthError.Reset();
	bRotationRetryUsed = false;

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
		PersistRefreshToken();
		SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::RefreshSucceeded));
		UE_LOG(LogMantlePlaceAuth, Log, TEXT("Token %s succeeded."),
			Kind == ERequestKind::Restore ? TEXT("restore") : TEXT("refresh"));
		NotifyTokenRefreshed(true);
		break;
	}
}

void UMantlePlaceAuthSystemBase::HandleAuthFailure(const FString& Message, ERequestKind Kind, bool bDefinitive)
{
	LastAuthError = Message;

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
	case ERequestKind::Restore:
		UE_LOG(LogMantlePlaceAuth, Warning, TEXT("Auth %s failed (%s): %s"),
			Kind == ERequestKind::Restore ? TEXT("restore") : TEXT("refresh"),
			bDefinitive ? TEXT("rejected") : TEXT("transient"), *Message);

		Tokens.Reset();
		ExpiresAtUtc = FDateTime(0);

		if (bDefinitive && !bRotationRetryUsed)
		{
			const FString Freshest = ReadFreshestRefreshToken();
			if (!Freshest.IsEmpty() && Freshest != PresentedRefreshToken)
			{
				bRotationRetryUsed = true;
				UE_LOG(LogMantlePlaceAuth, Log,
					TEXT("Refresh rejected a superseded token; the other host rotated it. Retrying once."));

				SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignOut));

				TryRestoreSession();
				return;
			}
		}

		if (bDefinitive)
		{
			ForgetStoredSession();
			LastAuthError = FString::Printf(
				TEXT("%s Sign in again to continue."), *Message);

			SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::SignOut));
		}
		else
		{
			SetAuthState(FMantlePlaceAuthLogic::NextState(AuthState, EMantlePlaceAuthEvent::RefreshFailed));
		}

		NotifyTokenRefreshed(false);
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
	OnAuthStateChanged(NewState);
	OnAuthStateChangedNative.Broadcast(NewState);
}

bool UMantlePlaceAuthSystemBase::StartLoopbackServer(FString& OutError)
{
	StopLoopbackServer();

	FHttpServerModule& ServerModule = FHttpServerModule::Get();

	ServerModule.StartAllListeners();

	FString CallbackPath = LoopbackCallbackPath;
	if (!CallbackPath.StartsWith(TEXT("/")))
	{
		CallbackPath = TEXT("/") + CallbackPath;
	}

	TWeakObjectPtr<UMantlePlaceAuthSystemBase> WeakThis(this);

	auto TryAcquire = [this, &ServerModule, &CallbackPath, WeakThis](int32 Port) -> bool
	{
		TSharedPtr<IHttpRouter> Router = ServerModule.GetHttpRouter(static_cast<uint32>(Port), true);
		if (!Router.IsValid())
		{
			return false;
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
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserErrorHtml(TEXT("The application is no longer running.")),
							TEXT("text/html")));
						return true;
					}

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
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserSuccessHtml(), TEXT("text/html")));
						Self->BeginPkceTokenExchange(*CodePtr);
					}
					else
					{
						OnComplete(FHttpServerResponse::Create(
							FMantlePlaceAuthLogic::BuildBrowserErrorHtml(FailMessage), TEXT("text/html")));
						const FString DeferredMessage = FailMessage;
						FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateWeakLambda(Self,
							[WeakThis, DeferredMessage](float) -> bool
							{
								if (UMantlePlaceAuthSystemBase* Late = WeakThis.Get())
								{
									Late->AbortBrowserSignIn(DeferredMessage, false);
								}
								return false;
							}), 0.0f);
					}
					return true;
				}));

		if (!Handle.IsValid())
		{
			return false;
		}

		LoopbackRouter = Router;
		CallbackRouteHandle = Handle;
		return true;
	};

	const FMantlePlaceAuthLogic::ELoopbackPortMode Mode =
	    FMantlePlaceAuthLogic::ResolveLoopbackPortMode(LoopbackPorts);

	int32 SelectedPort = 0;
	bool bAcquired = false;

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

	UE_LOG(LogMantlePlaceAuth, Log, TEXT("Sign-in callback listening on http://127.0.0.1:%d%s"),
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

	TSharedRef<FInternetAddr> Addr = Sockets->CreateInternetAddr();
	Addr->SetLoopbackAddress();
	Addr->SetPort(0);

	FUniqueSocket Probe = Sockets->CreateUniqueSocket(NAME_Stream, TEXT("MantlePlaceLoopbackProbe"));
	if (!Probe.IsValid())
	{
		return false;
	}

	Probe->SetReuseAddr(false);

	if (!Probe->Bind(*Addr))
	{
		return false;
	}

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
	const FString Body = FMantlePlaceAuthLogic::BuildPkceTokenBody(AuthCode, PendingCodeVerifier);
	SendAuthRequest(TokenEndpointUrl, Body, ERequestKind::PkceExchange);
}

void UMantlePlaceAuthSystemBase::AbortBrowserSignIn(const FString& Message, bool bUserAborted)
{
	StopLoopbackServer();
	StopSignInTimeout();
	CancelActiveRequest();
	PendingCodeVerifier.Reset();
	PendingState.Reset();
	bSignInCallbackConsumed = false;

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

void UMantlePlaceAuthSystemBase::NotifyTokenRefreshed(bool bSuccess)
{
	OnTokenRefreshed(bSuccess);
	OnTokenRefreshedNative.Broadcast(bSuccess);
}

FString UMantlePlaceAuthSystemBase::ReadFreshestRefreshToken()
{
	EnsureSecretStore();

	FString Stored;
	if (SecretStore.IsValid() && SecretStore->Load(GRefreshTokenKey, Stored) && !Stored.IsEmpty())
	{
		return Stored;
	}

	return Tokens.RefreshToken;
}

void UMantlePlaceAuthSystemBase::ForgetStoredSession()
{
	EnsureSecretStore();
	if (SecretStore.IsValid())
	{
		SecretStore->Clear(GRefreshTokenKey);
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

bool UMantlePlaceAuthSystemBase::OnSignInTimeout(float)
{
	SignInTimeoutTicker.Reset();
	if (AuthState == EMantlePlaceAuthState::Authenticating)
	{
		AbortBrowserSignIn(TEXT("Sign-in timed out. Please try again."), true);
	}
	return false;
}
