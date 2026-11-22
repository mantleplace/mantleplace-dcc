// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceBundleCache.generated.h"

class IHttpRequest;
class IHttpResponse;
class FArchive;

DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnDownloadCompleteNative, bool, const FString&, const FString&);
DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnDownloadProgressNative, const FMantlePlaceDownloadProgress&);

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
	 * Download an owned bundle's presigned URL into the local cache (streamed, async, fail-closed).
	 * Result via OnDownloadComplete; progress via OnDownloadProgress. The bundle is verified against
	 * the item's advertised size/sha256 before it is promoted; a failed/torn download never presents
	 * as a valid cache entry. An MPB 3.0 download (Presigned.Level set) is served from the slot only when
	 * the slot holds that level, and otherwise replaces it; the URL may then be empty for a cache hit.
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

	bool IsCachedAndValidAtLevel(const FMantlePlaceVaultItem& Item, const FString& Level) const;

	bool CachedHoldsView(const FMantlePlaceVaultItem& Item) const;

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

	FMantlePlaceOnDownloadCompleteNative OnDownloadCompleteNative;
	FMantlePlaceOnDownloadProgressNative OnDownloadProgressNative;

	virtual void BeginDestroy() override;

protected:
	FString GetCacheRoot() const;

private:
	void HandleDownloadProgress(uint64 BytesReceived);
	void HandleDownloadComplete(TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> Request,
		TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response, bool bConnectedSuccessfully);

	void FinishStream(bool bDeletePartFile);

	void FailDownload(const FString& Message);

	void NotifyDownloadComplete(bool bSuccess, const FString& LocalBundlePath, const FString& Message);
	void NotifyDownloadProgress(const FMantlePlaceDownloadProgress& Progress);

	FString ComputeFileSha256(const FString& Path, int64& OutSizeBytes) const;

	FMantlePlaceCachedBundle ResolveCached(const FMantlePlaceVaultItem& Item, const FString* WantedLevel = nullptr) const;

	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> ActiveRequest;

	TSharedPtr<FArchive> ResponseFileWriter;

	FMantlePlaceVaultItem ActiveItem;
	FString ActivePartPath;
	FString ActiveFinalPath;
	uint64 ExpectedTotalBytes = 0;
	bool bActiveIsView = false;
	int64 ActiveViewSizeBytes = 0;
	FString ActiveLevel;
};
