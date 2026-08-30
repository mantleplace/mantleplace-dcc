// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultClient.h"

#include "MantlePlaceVaultLogic.h"
#include "MantlePlaceAuthLogic.h"
#include "MantlePlaceAuthSystemBase.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceVault, Log, All);

void UMantlePlaceVaultClient::Initialize(UMantlePlaceAuthSystemBase* InAuthSystem)
{
	AuthSystem = InAuthSystem;
}

bool UMantlePlaceVaultClient::IsBundleIncomplete(const FMantlePlaceVaultItem& Item)
{
	return FMantlePlaceVaultLogic::IsIncompleteBundle(Item);
}

bool UMantlePlaceVaultClient::ShouldRecoverMissingUnrealPayload(
	bool bManifestReadable, bool bManifestValid, const FString& OrderId, bool bAlreadyRecovered)
{
	return FMantlePlaceVaultLogic::ShouldRecoverMissingUnrealPayload(
		bManifestReadable, bManifestValid, OrderId, bAlreadyRecovered);
}

FString UMantlePlaceVaultClient::GetBundleTierLabel(const FMantlePlaceVaultItem& Item)
{
	return FMantlePlaceVaultLogic::DeriveTierLabel(Item);
}

bool UMantlePlaceVaultClient::EnsureReady(FString& OutError, FString& OutJwt) const
{
	// Defense-in-depth against the hostless-URL bug class (a scheme-relative or scheme-only
	// value such as "//mantle.place" or "https:" that slips past an emptiness check and builds
	// a host-less request URL). Accepts http://localhost:<port> and LAN-IP http (dev) as well
	// as https:// (prod); rejects empty/relative/hostless values.
	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(VaultApiBaseUrl))
	{
		OutError = TEXT("Vault is not configured: set a valid VaultApiBaseUrl (http(s)://host[:port]).");
		return false;
	}
	if (!AuthSystem)
	{
		OutError = TEXT("Vault client not initialized: call Initialize(AuthSystem) first.");
		return false;
	}
	if (!AuthSystem->IsAuthenticated())
	{
		OutError = TEXT("Not signed in. Sign in before accessing the vault.");
		return false;
	}
	OutJwt = AuthSystem->GetAccessToken();
	if (OutJwt.IsEmpty())
	{
		OutError = TEXT("No access token available.");
		return false;
	}
	return true;
}

void UMantlePlaceVaultClient::ListVault()
{
	const TArray<FMantlePlaceVaultItem> Empty;

	FString Error;
	FString Jwt;
	if (!EnsureReady(Error, Jwt))
	{
		UE_LOG(LogMantlePlaceVault, Warning, TEXT("ListVault refused: %s"), *Error);
		NotifyVaultListed(false, Empty, Error);
		return;
	}

	CancelActiveRequest();

	const FString Url = FMantlePlaceVaultLogic::BuildListBundlesUrl(VaultApiBaseUrl);

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("GET"));
	ActiveRequest->SetURL(Url);
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *Jwt));

	// Weak pointer, never raw `this`: completion lands across frames; the owner may be GC'd.
	TWeakObjectPtr<UMantlePlaceVaultClient> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceVaultClient* Self = WeakThis.Get())
			{
				Self->HandleListResponse(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		ActiveRequest.Reset();
		NotifyVaultListed(false, Empty, TEXT("Failed to start the vault list request."));
	}
}

void UMantlePlaceVaultClient::GetPresignedUrl(const FString& OrderId, const FString& Format)
{
	const FMantlePlacePresignedDownload Empty;

	FString Error;
	FString Jwt;
	if (!EnsureReady(Error, Jwt))
	{
		UE_LOG(LogMantlePlaceVault, Warning, TEXT("GetPresignedUrl refused: %s"), *Error);
		NotifyPresigned(false, Empty, Error);
		return;
	}

	if (OrderId.IsEmpty())
	{
		NotifyPresigned(false, Empty, TEXT("OrderId is required."));
		return;
	}

	if (!FMantlePlaceVaultLogic::IsPresignableFormat(Format))
	{
		NotifyPresigned(false, Empty,
		                FString::Printf(
		                    TEXT("Unknown format '%s'. Expected 'bundle' for the whole archive, or one of glb, fbx, geotiff, cog, dwg, pmtiles."),
		                    *Format));
		return;
	}

	CancelActiveRequest();

	const FString Url = FMantlePlaceVaultLogic::BuildDownloadUrl(VaultApiBaseUrl, OrderId);
	const FString Body = FMantlePlaceVaultLogic::BuildDownloadBody(Format);

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("POST"));
	ActiveRequest->SetURL(Url);
	ActiveRequest->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *Jwt));
	ActiveRequest->SetContentAsString(Body);

	TWeakObjectPtr<UMantlePlaceVaultClient> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceVaultClient* Self = WeakThis.Get())
			{
				Self->HandleDownloadResponse(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		ActiveRequest.Reset();
		NotifyPresigned(false, Empty, TEXT("Failed to start the download-mint request."));
	}
}

void UMantlePlaceVaultClient::GetPresignedBundleUrl(const FString& OrderId)
{
	GetPresignedUrl(OrderId, FMantlePlaceVaultLogic::WholeBundleFormat());
}

void UMantlePlaceVaultClient::ProbePresignedUrl(const FString& Url)
{
	if (Url.IsEmpty())
	{
		OnPresignedUrlProbed(false, 0, TEXT("URL is empty."));
		return;
	}

	CancelActiveRequest();

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("GET"));
	ActiveRequest->SetURL(Url);
	// Ranged GET (1 byte): the R2 URL is SigV4-signed for GET, so a HEAD would fail the
	// signature. No Authorization header - the presigned query string carries its own auth.
	ActiveRequest->SetHeader(TEXT("Range"), TEXT("bytes=0-0"));

	TWeakObjectPtr<UMantlePlaceVaultClient> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceVaultClient* Self = WeakThis.Get())
			{
				Self->HandleProbeResponse(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		ActiveRequest.Reset();
		OnPresignedUrlProbed(false, 0, TEXT("Failed to start the probe request."));
	}
}

void UMantlePlaceVaultClient::HandleListResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	ActiveRequest.Reset();

	const TArray<FMantlePlaceVaultItem> Empty;

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		NotifyVaultListed(false, Empty, TEXT("Network error: no response from the platform."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FString Error;
		FString Code;
		if (!FMantlePlaceVaultLogic::ParseErrorBody(Content, Error, Code))
		{
			Error = FString::Printf(TEXT("HTTP %d"), ResponseCode);
		}
		NotifyVaultListed(false, Empty, Error);
		return;
	}

	TArray<FMantlePlaceVaultItem> Items;
	FString ParseError;
	TArray<FString> Warnings;
	if (!FMantlePlaceVaultLogic::ParseListResponse(Content, Items, ParseError, &Warnings))
	{
		NotifyVaultListed(false, Empty, ParseError);
		return;
	}

	FString Message = FString::Printf(TEXT("%d bundle(s)."), Items.Num());
	if (Warnings.Num() > 0)
	{
		UE_LOG(LogMantlePlaceVault, Warning, TEXT("Vault list: %d entr(ies) skipped."), Warnings.Num());
		Message += FString::Printf(TEXT(" (%d skipped: %s)"), Warnings.Num(), *FString::Join(Warnings, TEXT("; ")));
	}
	UE_LOG(LogMantlePlaceVault, Log, TEXT("Vault list returned %d bundle(s)."), Items.Num());
	NotifyVaultListed(true, Items, Message);
}

void UMantlePlaceVaultClient::HandleDownloadResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	ActiveRequest.Reset();

	const FMantlePlacePresignedDownload Empty;

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		NotifyPresigned(false, Empty, TEXT("Network error: no response from the platform."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FString Error;
		FString Code;
		if (!FMantlePlaceVaultLogic::ParseErrorBody(Content, Error, Code))
		{
			Error = FString::Printf(TEXT("HTTP %d"), ResponseCode);
		}
		else if (!Code.IsEmpty())
		{
			// Surface the machine code (e.g. refunded / revoked on a 410) alongside the message.
			Error = FString::Printf(TEXT("%s (%s)"), *Error, *Code);
		}
		NotifyPresigned(false, Empty, Error);
		return;
	}

	FMantlePlacePresignedDownload Download;
	FString ParseError;
	if (!FMantlePlaceVaultLogic::ParseDownloadResponse(Content, Download, ParseError))
	{
		NotifyPresigned(false, Empty, ParseError);
		return;
	}

	UE_LOG(LogMantlePlaceVault, Log, TEXT("Minted presigned URL (expires %s)."), *Download.ExpiresAt);
	NotifyPresigned(true, Download, TEXT("Presigned URL minted."));
}

void UMantlePlaceVaultClient::HandleProbeResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	ActiveRequest.Reset();

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		OnPresignedUrlProbed(false, 0, TEXT("Network error probing the URL."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	// A ranged GET resolves with 206 Partial Content (or 200 if the server ignores Range).
	const bool bResolves = (ResponseCode == 200 || ResponseCode == 206);
	OnPresignedUrlProbed(bResolves, ResponseCode,
		bResolves ? TEXT("URL resolves.") : FString::Printf(TEXT("Probe returned HTTP %d."), ResponseCode));
}

void UMantlePlaceVaultClient::RequestMaterialize(const FString& OrderId, const FString& Scope)
{
	FString Error;
	FString Jwt;
	if (!EnsureReady(Error, Jwt))
	{
		UE_LOG(LogMantlePlaceVault, Warning, TEXT("RequestMaterialize refused: %s"), *Error);
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), Error);
		return;
	}

	if (OrderId.IsEmpty())
	{
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), TEXT("OrderId is required."));
		return;
	}

	if (!FMantlePlaceVaultLogic::IsValidMaterializeScope(Scope))
	{
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(),
		                         FString::Printf(TEXT("Unknown materialize scope '%s'. Expected 'unreal' or 'all'."), *Scope));
		return;
	}

	CancelActiveRequest();

	const FString Url = FMantlePlaceVaultLogic::BuildMaterializeUrl(VaultApiBaseUrl, OrderId);
	const FString Body = FMantlePlaceVaultLogic::BuildMaterializeBody(Scope);

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("POST"));
	ActiveRequest->SetURL(Url);
	ActiveRequest->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *Jwt));
	ActiveRequest->SetContentAsString(Body);

	TWeakObjectPtr<UMantlePlaceVaultClient> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceVaultClient* Self = WeakThis.Get())
			{
				Self->HandleMaterializeStartResponse(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		ActiveRequest.Reset();
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), TEXT("Failed to start the materialize request."));
	}
}

void UMantlePlaceVaultClient::GetMaterializeStatus(const FString& OrderId, const TArray<FString>& Requested)
{
	// An empty set means the caller had nothing better; fall back to this host's own list rather
	// than polling with no yardstick, which can never conclude.
	PendingStatusTokens = Requested.Num() > 0 ? Requested : FMantlePlaceVaultLogic::TargetedImportTokens();

	const FMantlePlaceMaterializeStatus Empty;

	FString Error;
	FString Jwt;
	if (!EnsureReady(Error, Jwt))
	{
		UE_LOG(LogMantlePlaceVault, Warning, TEXT("GetMaterializeStatus refused: %s"), *Error);
		NotifyMaterializeStatus(false, Empty, Error);
		return;
	}

	if (OrderId.IsEmpty())
	{
		NotifyMaterializeStatus(false, Empty, TEXT("OrderId is required."));
		return;
	}

	CancelActiveRequest();

	const FString Url = FMantlePlaceVaultLogic::BuildMaterializeUrl(VaultApiBaseUrl, OrderId);

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("GET"));
	ActiveRequest->SetURL(Url);
	ActiveRequest->SetHeader(TEXT("Accept"), TEXT("application/json"));
	ActiveRequest->SetHeader(TEXT("Authorization"), FString::Printf(TEXT("Bearer %s"), *Jwt));

	TWeakObjectPtr<UMantlePlaceVaultClient> WeakThis(this);
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceVaultClient* Self = WeakThis.Get())
			{
				Self->HandleMaterializeStatusResponse(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		ActiveRequest.Reset();
		NotifyMaterializeStatus(false, Empty, TEXT("Failed to start the materialize status request."));
	}
}

void UMantlePlaceVaultClient::HandleMaterializeStartResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	ActiveRequest.Reset();

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), TEXT("Network error: no response from the platform."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	// 409 (single-flight: a materialize is already running) is not an OK code but is a success for us -
	// the body carries the activeJobId, which we poll. Any other non-2xx is a genuine failure.
	const bool bAcceptedCode = EHttpResponseCodes::IsOk(ResponseCode) || ResponseCode == EHttpResponseCodes::Conflict;
	if (!bAcceptedCode)
	{
		FString Error;
		FString Code;
		if (!FMantlePlaceVaultLogic::ParseErrorBody(Content, Error, Code))
		{
			Error = FString::Printf(TEXT("HTTP %d"), ResponseCode);
		}
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), Error);
		return;
	}

	FMantlePlaceMaterializeStart Start;
	FString ParseError;
	if (!FMantlePlaceVaultLogic::ParseMaterializeStartResponse(Content, Start, ParseError))
	{
		NotifyMaterializeStarted(false, FMantlePlaceMaterializeStart(), ParseError);
		return;
	}

	FString Message;
	switch (Start.Outcome)
	{
	case EMantlePlaceMaterializeStartOutcome::Joined:
		Message = TEXT("A materialize job is already running for this order - tracking it.");
		break;
	case EMantlePlaceMaterializeStartOutcome::NothingToDo:
		Message = TEXT("This bundle already has everything that was asked for.");
		break;
	case EMantlePlaceMaterializeStartOutcome::Queued:
		Message = TEXT("The order is still building; these formats are queued and start on their own.");
		break;
	default:
		Message = TEXT("Generating Unreal formats.");
		break;
	}

	UE_LOG(LogMantlePlaceVault, Log, TEXT("Materialize outcome %d (job '%s', %d token(s))."),
	       static_cast<int32>(Start.Outcome), *Start.JobId, Start.Tokens.Num());
	NotifyMaterializeStarted(true, Start, Message);
}

void UMantlePlaceVaultClient::HandleMaterializeStatusResponse(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	ActiveRequest.Reset();

	const FMantlePlaceMaterializeStatus Empty;

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		NotifyMaterializeStatus(false, Empty, TEXT("Network error polling materialize status."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	const FString Content = Response->GetContentAsString();

	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FString Error;
		FString Code;
		if (!FMantlePlaceVaultLogic::ParseErrorBody(Content, Error, Code))
		{
			Error = FString::Printf(TEXT("HTTP %d"), ResponseCode);
		}
		NotifyMaterializeStatus(false, Empty, Error);
		return;
	}

	FMantlePlaceMaterializeStatus Status;
	FString ParseError;
	if (!FMantlePlaceVaultLogic::ParseMaterializeStatus(Content, PendingStatusTokens, Status, ParseError))
	{
		NotifyMaterializeStatus(false, Empty, ParseError);
		return;
	}

	NotifyMaterializeStatus(true, Status, Status.Message);
}

void UMantlePlaceVaultClient::NotifyVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	OnVaultListedNative.Broadcast(bSuccess, Bundles, Message);
	OnVaultListed(bSuccess, Bundles, Message);
}

void UMantlePlaceVaultClient::NotifyPresigned(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message)
{
	OnPresignedUrlReadyNative.Broadcast(bSuccess, Download, Message);
	OnPresignedUrlReady(bSuccess, Download, Message);
}

void UMantlePlaceVaultClient::NotifyMaterializeStarted(bool bSuccess, const FMantlePlaceMaterializeStart& Start, const FString& Message)
{
	OnMaterializeStartedNative.Broadcast(bSuccess, Start, Message);
	OnMaterializeStarted(bSuccess, Start, Message);
}

void UMantlePlaceVaultClient::NotifyMaterializeStatus(bool bOk, const FMantlePlaceMaterializeStatus& Status, const FString& Message)
{
	OnMaterializeStatusNative.Broadcast(bOk, Status, Message);

	// Route the Blueprint surface: a failed poll or a non-terminal state is "progress"; a terminal
	// state (complete/failed) is "complete".
	if (bOk && (Status.State == EMantlePlaceMaterializeState::Complete || Status.State == EMantlePlaceMaterializeState::Failed))
	{
		OnMaterializeComplete(Status.State == EMantlePlaceMaterializeState::Complete, Status, Message);
	}
	else
	{
		OnMaterializeProgress(Status);
	}
}

void UMantlePlaceVaultClient::CancelActiveRequest()
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

void UMantlePlaceVaultClient::BeginDestroy()
{
	CancelActiveRequest();
	Super::BeginDestroy();
}
