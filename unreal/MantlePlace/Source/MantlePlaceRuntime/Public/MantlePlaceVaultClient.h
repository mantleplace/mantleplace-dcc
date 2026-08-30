// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceVaultClient.generated.h"

class IHttpRequest;
class IHttpResponse;
class UMantlePlaceAuthSystemBase;

/**
 * Native (C++) completion delegates, broadcast alongside the Blueprint events below so a C++
 * orchestrator (the editor vault-import facade) can drive the async vault flow without a
 * reparented Blueprint child. Not UPROPERTY (native delegates cannot be) - bind with AddUObject.
 */
DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnVaultListedNative, bool /*bSuccess*/, const TArray<FMantlePlaceVaultItem>& /*Bundles*/, const FString& /*Message*/);
DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnPresignedNative, bool /*bSuccess*/, const FMantlePlacePresignedDownload& /*Download*/, const FString& /*Message*/);
DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnMaterializeStartedNative, bool /*bSuccess*/, const FMantlePlaceMaterializeStart& /*Start*/, const FString& /*Message*/);
DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnMaterializeStatusNative, bool /*bOk*/, const FMantlePlaceMaterializeStatus& /*Status*/, const FString& /*Message*/);

/**
 * C++ client for the Mantle Place vault API - this host's consumer of the platform's two
 * plugin-facing endpoints:
 *
 *   - ListVault()        -> GET  /api/v1/vault/bundles
 *   - GetPresignedUrl()  -> POST /api/v1/vault/bundles/{orderId}/download
 *   - ProbePresignedUrl()-> ranged GET against the minted R2 URL (resolves? no download)
 *
 * Owns the impure HTTP lifecycle; the deterministic URL/body/parse logic lives in the
 * headless-testable FMantlePlaceVaultLogic. Requests are async/non-blocking
 * (FHttpModule) so the editor tick is never blocked. The JWT comes from the P5 auth base
 * (UMantlePlaceAuthSystemBase) passed to Initialize(); this class never persists it.
 *
 * A human reparents a Blueprint child (the vault panel / EUW) onto this base and wires
 * only the surface - implementing the OnVaultListed / OnPresignedUrlReady /
 * OnPresignedUrlProbed events to drive UI.
 *
 * Configure via DefaultGame.ini [/Script/MantlePlaceRuntime.MantlePlaceVaultClient]:
 * VaultApiBaseUrl (the Mantle Place web app host - distinct from the Supabase auth URL).
 */
UCLASS(BlueprintType, Blueprintable, config = Game)
class MANTLEPLACERUNTIME_API UMantlePlaceVaultClient : public UObject
{
	GENERATED_BODY()

public:
	/**
	 * Mantle Place web app base URL serving /api/v1/vault/*. Public route, compiled in
	 * (mirrors the Revit host's MantlePlaceEndpoints.ApiBaseUrl); override via config
	 * (e.g. http://localhost:3000) to point at a local web dev server. No trailing slash
	 * required.
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Vault")
	FString VaultApiBaseUrl = TEXT("https://mantle.place");

	/** Wire the auth source (the signed-in P5 auth base) whose JWT authorizes vault calls. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void Initialize(UMantlePlaceAuthSystemBase* InAuthSystem);

	/** Begin listing the signed-in curator's owned bundles. Result via OnVaultListed. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void ListVault();

	/**
	 * Begin minting a presigned download URL for one owned bundle + format.
	 * OrderId is FMantlePlaceVaultItem.OrderId; Format must be one of
	 * glb | fbx | geotiff | cog | dwg | pmtiles, or "bundle" for the whole archive.
	 * Result via OnPresignedUrlReady.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void GetPresignedUrl(const FString& OrderId, const FString& Format);

	/**
	 * Begin minting a presigned URL for the whole packaged archive -- what the importer wants, and
	 * what the bundle cache's sha256 describes.
	 *
	 * Exists so callers never spell the token themselves. It lives in the Runtime module's PRIVATE
	 * logic header, unreachable from the Editor module, and the last caller to work around that
	 * hardcoded the platform's deprecated ambiguous alias instead. Result via OnPresignedUrlReady.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void GetPresignedBundleUrl(const FString& OrderId);

	/**
	 * Probe a minted URL with a 1-byte ranged GET (no auth header) to confirm it resolves
	 * without downloading the bundle. Result via OnPresignedUrlProbed.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void ProbePresignedUrl(const FString& Url);

	/**
	 * Begin an on-demand "Generate Unreal formats" (materialize) for one owned BASE bundle. Scope is
	 * "unreal" (heightmap + drape + terrain mesh + buildings) or "all" (the full on-demand set). The
	 * ETL generates the requested formats so a native import becomes possible. Result via
	 * OnMaterializeStarted; a 409 single-flight (a job is already running) is reported as success.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void RequestMaterialize(const FString& OrderId, const FString& Scope);

	/**
	 * Poll a materialize job's status once (GET). A non-terminal state fires OnMaterializeProgress;
	 * a terminal state (complete/failed) fires OnMaterializeComplete. The repeat-until-done loop is
	 * the caller's (the orchestrator polls on an interval until complete).
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	/**
	 * Poll the materialize status.
	 *
	 * Requested is the token set whose delivery decides completion. The platform answers this
	 * endpoint with a delivery-state document carrying no status word, so without it there is nothing
	 * to compare against and no way to know a build has finished.
	 */
	void GetMaterializeStatus(const FString& OrderId, const TArray<FString>& Requested);

	/**
	 * True iff Item is an incomplete (BASE) bundle that still needs "Generate Unreal formats" - it
	 * advertises formats but no "glb" terrain mesh. Thin public wrapper over the tested vault logic
	 * so the editor surface + orchestrator can classify a list row without the private logic header.
	 */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	static bool IsBundleIncomplete(const FMantlePlaceVaultItem& Item);

	/** Short tier label for a vault list row: "Base" (needs materialize), "Unreal" (importable), or "Unknown". */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	static FString GetBundleTierLabel(const FMantlePlaceVaultItem& Item);

	/**
	 * True iff a downloaded bundle read as incomplete should be completed in the cloud before
	 * import. Thin wrapper over the tested vault logic so the orchestrator can decide the
	 * post-download self-heal without the private logic header.
	 */
	static bool ShouldRecoverMissingUnrealPayload(
		bool bManifestReadable, bool bManifestValid, const FString& OrderId, bool bAlreadyRecovered);

	/** Implemented by the Blueprint child: vault list finished. */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);

	/** Implemented by the Blueprint child: a presigned download URL was minted (or failed). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnPresignedUrlReady(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message);

	/** Implemented by the Blueprint child: a URL probe finished (HTTP status reported). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnPresignedUrlProbed(bool bSuccess, int32 HttpStatus, const FString& Message);

	/** Implemented by the Blueprint child: a materialize request was accepted (or refused). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnMaterializeStarted(bool bSuccess, const FMantlePlaceMaterializeStart& Start, const FString& Message);

	/** Implemented by the Blueprint child: a non-terminal materialize status poll (pending/processing). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnMaterializeProgress(const FMantlePlaceMaterializeStatus& Status);

	/** Implemented by the Blueprint child: a terminal materialize status poll (complete or failed). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnMaterializeComplete(bool bSuccess, const FMantlePlaceMaterializeStatus& Status, const FString& Message);

	//~ Native completion delegates (C++ orchestrator hooks; fired alongside the Blueprint events).
	FMantlePlaceOnVaultListedNative OnVaultListedNative;
	FMantlePlaceOnPresignedNative OnPresignedUrlReadyNative;
	FMantlePlaceOnMaterializeStartedNative OnMaterializeStartedNative;

	/**
	 * The tokens the in-flight status poll measures delivery against.
	 *
	 * Held on the client because the status response is a delivery-state document with no status
	 * word: without the requested set there is nothing to compare `delivered` to, and no way to tell
	 * a finished build from one that has not started.
	 */
	TArray<FString> PendingStatusTokens;
	FMantlePlaceOnMaterializeStatusNative OnMaterializeStatusNative;

	//~ Begin UObject interface
	virtual void BeginDestroy() override;
	//~ End UObject interface

private:
	/** Validate config + auth and fetch the JWT; fills OutError on failure. */
	bool EnsureReady(FString& OutError, FString& OutJwt) const;

	/** HTTP completion handlers (game thread): parse + fire the relevant event. */
	void HandleListResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);
	void HandleDownloadResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);
	void HandleProbeResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);
	void HandleMaterializeStartResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);
	void HandleMaterializeStatusResponse(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);

	/** Fire the native delegate + the Blueprint event together (single source of truth per signal). */
	void NotifyVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void NotifyPresigned(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message);
	void NotifyMaterializeStarted(bool bSuccess, const FMantlePlaceMaterializeStart& Start, const FString& Message);
	void NotifyMaterializeStatus(bool bOk, const FMantlePlaceMaterializeStatus& Status, const FString& Message);

	/** Unbind + cancel + reset any in-flight request. */
	void CancelActiveRequest();

	/** The auth base supplying the JWT (kept referenced so it isn't GC'd out from under us). */
	UPROPERTY()
	TObjectPtr<UMantlePlaceAuthSystemBase> AuthSystem;

	/** The in-flight request (single at a time; the HTTP module also retains it). */
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> ActiveRequest;
};
