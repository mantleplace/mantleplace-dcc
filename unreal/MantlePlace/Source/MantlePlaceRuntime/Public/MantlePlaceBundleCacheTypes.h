// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleCacheTypes.generated.h"

/**
 * Cloud-vs-cached state of an owned bundle in the local vault cache
 *. Drives the editor list's
 * per-row badge. A bundle is owned offline once `CachedValid`.
 */
UENUM(BlueprintType)
enum class EMantlePlaceCacheState : uint8
{
	/** No cached file on disk for this order - must be downloaded. */
	NotCached,
	/** Cached file present and passed the validity check - re-importable offline. */
	CachedValid,
	/** Cached file present but failed integrity/size/version - re-download to refresh. */
	CachedStale
};

/**
 * Why a cached file was judged invalid. `None` accompanies a valid verdict. Surfaced so the
 * UI (and the fail-closed download path) can explain a stale cache to the curator.
 */
UENUM(BlueprintType)
enum class EMantlePlaceCacheInvalidReason : uint8
{
	/** Valid - no problem. */
	None,
	/** No file on disk. */
	Missing,
	/** On-disk size disagrees with the vault's advertised whole-bundle size. */
	SizeMismatch,
	/** Computed sha256 disagrees with the vault's advertised whole-bundle hash (re-cut or corruption). */
	Sha256Mismatch,
	/** The bundle's manifest version predates the minimum the Importer supports. */
	ManifestTooOld
};

/**
 * Verdict from the pure cache-validity decision. `bIntegrityChecked` is false when the sha256
 * could not be (or was deliberately not) computed - e.g. a legacy bundle with no advertised
 * hash, or a file above the hashing size cap - in which case `bValid` rests on size+version
 * alone ("valid but unverified", mirroring the vault client's null=unknown rule).
 */
USTRUCT(BlueprintType)
struct FMantlePlaceCacheValidity
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bValid = false;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	EMantlePlaceCacheInvalidReason Reason = EMantlePlaceCacheInvalidReason::Missing;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bIntegrityChecked = false;
};

/**
 * A bundle resident in the local vault cache, as recorded in the per-order `cache.json` sidecar
 * and reported back to the editor list. `LocalPath` is what `gui_bridge.run_import` consumes.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceCachedBundle
{
	GENERATED_BODY()

	/** Order id this cache entry belongs to. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString OrderId;

	/** Absolute path to the cached bundle.zip (empty when NotCached). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString LocalPath;

	/** sha256 recorded for the cached bytes (empty if never hashed). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Sha256;

	/** On-disk size of the cached bundle in bytes. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int64 SizeBytes = 0;

	/**
	 * Manifest version recorded for the cached bundle; empty if unknown.
	 *
	 * A string, spanning both version families: a bundle cached before the MPB re-baseline
	 * recorded the integer era's "19", one cached after records "1.0.0". Empty means the sidecar
	 * reported nothing at all, which is NOT the same as reporting something old (HPS-20).
	 */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString ManifestVersion;

	/** ISO-8601 UTC stamp of when the bundle was cached. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString DownloadedAtUtc;

	/** Download format the cached bundle was minted as (e.g. "glb"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Format;

	/** Cloud-vs-cached state for the list row. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	EMantlePlaceCacheState State = EMantlePlaceCacheState::NotCached;
};

/**
 * Streamed-download progress, marshaled to the game thread before the Blueprint event fires.
 * `Fraction` is -1 when the total size is unknown (chunked transfer with no Content-Length) so
 * the UI can show an indeterminate bar.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceDownloadProgress
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int64 BytesReceived = 0;

	/** Total bytes expected, or 0 if unknown. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int64 TotalBytes = 0;

	/** 0..1 progress, or -1 when TotalBytes is unknown. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	float Fraction = -1.0f;
};
