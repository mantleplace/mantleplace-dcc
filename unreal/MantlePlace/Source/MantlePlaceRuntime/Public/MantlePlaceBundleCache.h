// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceBundleCache.generated.h"

class IHttpRequest;
class IHttpResponse;
class FArchive;

/**
 * Native (C++) download delegates, broadcast alongside the Blueprint events so a C++ orchestrator
 * (the editor vault-import facade) can observe the streamed download without a reparented Blueprint
 * child. Not UPROPERTY (native delegates cannot be) - bind with AddUObject.
 */
DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnDownloadCompleteNative, bool /*bSuccess*/, const FString& /*LocalBundlePath*/, const FString& /*Message*/);
DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnDownloadProgressNative, const FMantlePlaceDownloadProgress& /*Progress*/);

/**
 * Local vault bundle cache for the vault-in-editor import flow
 *.
 *
 * Streams a presigned bundle download straight to disk under <Project>/Saved/ (owned, offline,
 * re-importable forever - the anti-streaming guarantee), reporting non-blocking progress and
 * verifying integrity fail-closed before the bytes are promoted into the cache. The cached
 * bundle.zip path is what the Python Importer (gui_bridge.run_import) consumes.
 *
 * Owns the impure IO; the deterministic path/validity/expiry/sha logic lives in the headless-
 * testable FMantlePlaceBundleCacheLogic. A human reparents a Blueprint child (the vault EUW)
 * onto this base and wires only the surface - implementing OnDownloadProgress / OnDownloadComplete
 * to drive the UI.
 *
 * Configure via DefaultGame.ini [/Script/MantlePlaceRuntime.MantlePlaceBundleCache].
 */
UCLASS(BlueprintType, Blueprintable, config = Game)
class MANTLEPLACERUNTIME_API UMantlePlaceBundleCache : public UObject
{
	GENERATED_BODY()

public:
	/** Cache root relative to the project's Saved/ dir. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Vault")
	FString CacheSubDir = TEXT("MantlePlace/VaultCache");

	/**
	 * Largest bundle (bytes) we sha256-verify by loading into memory; above this, integrity falls
	 * back to size (+ manifest version), surfaced as valid-but-unverified. Bounded by TArray<uint8>
	 * int32 indexing (~2 GiB).
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Config, Category = "Mantle Place|Vault")
	int64 MaxHashSizeBytes = 2147483647;

	/**
	 * Download an owned bundle's presigned URL into the local cache (streamed, async, fail-closed).
	 * Result via OnDownloadComplete; progress via OnDownloadProgress. The bundle is verified against
	 * the item's advertised size/sha256 before it is promoted; a failed/torn download never presents
	 * as a valid cache entry.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void DownloadBundle(const FMantlePlaceVaultItem& Item, const FMantlePlacePresignedDownload& Presigned);

	/** Cancel any in-flight download (no OnDownloadComplete is fired for a user-cancelled request). */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void CancelDownload();

	/** Absolute path the cached bundle.zip would occupy for this item (whether or not it exists). */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	FString GetCachedBundlePath(const FMantlePlaceVaultItem& Item) const;

	/**
	 * Resolve the cloud-vs-cached state for one item (cheap: stat + the recorded sidecar sha, no
	 * re-hash). Detects a re-cut bundle (cached sha != the vault's current sha -> CachedStale).
	 * Also fires OnCacheStateResolved so a Blueprint list row can update its badge.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	FMantlePlaceCachedBundle InspectCache(const FMantlePlaceVaultItem& Item);

	/** True iff a valid cached bundle exists for this item (the offline re-import gate). */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	bool IsCachedAndValid(const FMantlePlaceVaultItem& Item) const;

	/** Delete this item's cache dir (user-owned cache management). Returns true if it is now gone. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	bool EvictCache(const FMantlePlaceVaultItem& Item);

	/** Implemented by the Blueprint child: streamed-download progress (game thread). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnDownloadProgress(const FMantlePlaceDownloadProgress& Progress);

	/** Implemented by the Blueprint child: download finished. LocalBundlePath is empty on failure. */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnDownloadComplete(bool bSuccess, const FString& LocalBundlePath, const FString& Message);

	/** Implemented by the Blueprint child: a cache-state inspection finished (drives the list badge). */
	UFUNCTION(BlueprintImplementableEvent, Category = "Mantle Place|Vault")
	void OnCacheStateResolved(const FMantlePlaceVaultItem& Item, const FMantlePlaceCachedBundle& Cached);

	//~ Native download delegates (C++ orchestrator hooks; fired alongside the Blueprint events).
	FMantlePlaceOnDownloadCompleteNative OnDownloadCompleteNative;
	FMantlePlaceOnDownloadProgressNative OnDownloadProgressNative;

	//~ Begin UObject interface
	virtual void BeginDestroy() override;
	//~ End UObject interface

protected:
	/** Absolute cache root: <ProjectSavedDir>/<CacheSubDir>. */
	FString GetCacheRoot() const;

private:
	/** HTTP streamed-download handlers (game thread under CompleteOnGameThread policy). */
	void HandleDownloadProgress(uint64 BytesReceived);
	void HandleDownloadComplete(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);

	/** Close the streamed writer, drop the request, and (on failure) delete the partial file. */
	void FinishStream(bool bDeletePartFile);

	/** Report a failed download: clean up the partial file and fire OnDownloadComplete(false). */
	void FailDownload(const FString& Message);

	/** Fire the native delegate + the Blueprint event together (single source of truth per signal). */
	void NotifyDownloadComplete(bool bSuccess, const FString& LocalBundlePath, const FString& Message);
	void NotifyDownloadProgress(const FMantlePlaceDownloadProgress& Progress);

	/** sha256 of a file, or empty when missing/over the size cap (size-only path). */
	FString ComputeFileSha256(const FString& Path, int64& OutSizeBytes) const;

	/** Resolve cache state for an item (stat + sidecar, no event). Shared by Inspect/IsValid/Download. */
	FMantlePlaceCachedBundle ResolveCached(const FMantlePlaceVaultItem& Item) const;

	/** The in-flight request (single at a time; the HTTP module also retains it). */
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> ActiveRequest;

	/** The streamed-to-disk sink for the active download (the .part file). */
	TSharedPtr<FArchive> ResponseFileWriter;

	/** Active-download bookkeeping (valid only while a request is in flight). */
	FMantlePlaceVaultItem ActiveItem;
	FString ActivePartPath;
	FString ActiveFinalPath;
	uint64 ExpectedTotalBytes = 0;
};
