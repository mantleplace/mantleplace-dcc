// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion

/**
 * Pure (filesystem-/network-free) logic for the local vault bundle cache
 *.
 *
 * Everything here is deterministic and headless-testable: cache-path derivation, the
 * cache-validity decision (fed measured facts, never the filesystem), cloud-vs-cached state,
 * presigned-URL expiry math, sha256 comparison, and cache.json (de)serialization. The UObject
 * shim (UMantlePlaceBundleCache) owns the impure parts - streaming HTTP download, hashing
 * the file on disk, IFileManager stat/move, and firing Blueprint events. Keep this layer free
 * of HTTP/UObject dependencies so the automation test can exercise it under -nullrhi (mirrors
 * FMantlePlaceVaultLogic / FMantlePlaceAuthLogic).
 */
/**
 * Incremental FIPS-180-4 SHA-256 so a multi-GB file can be hashed in fixed-size chunks without
 * loading it whole into memory. Construct, Update() repeatedly, then Final() once for the
 * lowercase-hex digest. FMantlePlaceBundleCacheLogic::Sha256Hex() is the one-shot wrapper.
 */
struct FMantlePlaceSha256
{
	FMantlePlaceSha256();

	/** Feed bytes; may be called repeatedly to hash a stream in fixed-size chunks. */
	void Update(const uint8* Data, int64 NumBytes);

	/** Finalize and return the lowercase-hex digest (do not Update afterwards). */
	FString Final();

private:
	uint32 H[8];
	uint8 Pending[64];
	int32 PendingLen = 0;
	uint64 TotalBytes = 0;
};

struct FMantlePlaceBundleCacheLogic
{
	// ── Path derivation (deterministic; the shim supplies CacheRoot so this stays IO-free) ──

	/**
	 * Make an order id safe as a single path segment: keep alphanumerics plus '.', '_' and '-',
	 * replace the rest with '_'. "Alphanumeric" is FChar::IsAlnum — Unicode-aware, NOT ASCII-only,
	 * so a non-Latin order id survives unchanged and keeps its existing cache path (HPS-30).
	 *
	 * Enumeration is over CODE POINTS, never code units: a surrogate pair is one character and
	 * earns one underscore. Classification is bounded to the BMP, so every code point above
	 * U+FFFF is non-alphanumeric here regardless of its Unicode category — FChar::IsAlnum takes a
	 * TCHAR and cannot answer for one, and the alternative is a vendored Unicode table per host.
	 */
	static FString SanitizeKeySegment(const FString& OrderId);

	/** "<CacheRoot>/<sanitized OrderId>". */
	static FString DeriveBundleDir(const FString& CacheRoot, const FString& OrderId);

	/** "<CacheRoot>/<sanitized OrderId>/bundle.zip" - the cached bundle run_import consumes. */
	static FString DeriveBundlePath(const FString& CacheRoot, const FString& OrderId);

	/** "<...>/bundle.zip.part" - the staging file a download streams into before the atomic rename. */
	static FString DerivePartPath(const FString& CacheRoot, const FString& OrderId);

	/** "<...>/cache.json" - the per-order sidecar recording integrity facts. */
	static FString DeriveMetaPath(const FString& CacheRoot, const FString& OrderId);

	// ── Cache-validity decision (PURE: takes facts, not the filesystem) ──

	/**
	 * Decide whether a cached file is valid, fail-closed but legacy-tolerant. ComputedSha256 may
	 * be empty ("not hashed" - legacy bundle or above the size cap), in which case validity rests
	 * on size+version and bIntegrityChecked is false. Expected* come from the vault list item
	 * (the bHas* flags carry the "null = unknown" rule). MinVersion is the Importer minimum.
	 */
	static FMantlePlaceCacheValidity DecideValidity(
		bool bFileExists,
		int64 OnDiskSizeBytes,
		const FString& ComputedSha256,
		bool bHasExpectedSha,
		const FString& ExpectedSha256,
		bool bHasExpectedSize,
		int64 ExpectedSizeBytes,
		bool bHasManifestVersion,
		const FString& ManifestVersion,
		const FString& MinVersion = MantlePlaceMinSupportedManifestVersion);

	/** Map (file present?, validity) to the list-row state. */
	static EMantlePlaceCacheState DeriveCacheState(bool bFileExists, const FMantlePlaceCacheValidity& Validity);

	// ── Presigned-URL expiry (ISO-8601) - mirrors the auth IsExpired shape ──

	/** Parse an ISO-8601 timestamp (e.g. "2026-06-20T00:00:00.000Z") to a UTC FDateTime. */
	static bool ParseExpiry(const FString& Iso8601, FDateTime& OutUtc);

	/** True if a URL expiring at ExpiresAtUtc should be treated as expired at NowUtc (with skew). */
	static bool IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc, int32 SkewSeconds = 60);

	// ── sha256 hex compare (case-insensitive, trimmed) ──
	static bool Sha256Equal(const FString& A, const FString& B);

	/**
	 * Lowercase-hex SHA-256 of a buffer. Self-contained (UE's FGenericPlatformMisc::GetSHA256Signature
	 * is an unimplemented stub on this build and there is no platform override) - used for bundle
	 * integrity, not secrecy, and pinned by a known-answer test. NumBytes is int64 but the shim only
	 * feeds it a buffer loaded under MaxHashSizeBytes (bounded by TArray<uint8> int32 indexing).
	 */
	static FString Sha256Hex(const uint8* Data, int64 NumBytes);

	// ── cache.json sidecar (de)serialization (PURE string<->struct) ──

	/** Serialize the cache sidecar to a condensed JSON string. */
	static FString SerializeMeta(const FMantlePlaceCachedBundle& Meta);

	/** Parse a cache sidecar; fail-closed (returns false + OutError) on malformed JSON or missing orderId. */
	static bool ParseMeta(const FString& Json, FMantlePlaceCachedBundle& Out, FString& OutError);
};
