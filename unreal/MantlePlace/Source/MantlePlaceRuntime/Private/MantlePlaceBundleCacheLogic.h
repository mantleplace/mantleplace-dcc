// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h"

struct FMantlePlaceBundleCacheLogic
{
	static FString SanitizeKeySegment(const FString& OrderId);

	static FString DeriveBundleDir(const FString& CacheRoot, const FString& OrderId);

	static FString DeriveBundlePath(const FString& CacheRoot, const FString& OrderId);

	static FString DerivePartPath(const FString& CacheRoot, const FString& OrderId);

	static FString DeriveMetaPath(const FString& CacheRoot, const FString& OrderId);

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

	static FMantlePlaceCacheExpectation ExpectationForHeld(
		bool bHoldsView,
		const FString& RecordedSha256,
		int64 RecordedSizeBytes,
		bool bListingHasSha256,
		const FString& ListingSha256,
		bool bListingHasSizeBytes,
		int64 ListingSizeBytes);

	static bool IsViewRecut(
		bool bHoldsView, const FString& RecordedListingSha256, bool bListingHasSha256, const FString& ListingSha256);

	static FMantlePlaceCacheExpectation ExpectationForDownload(
		bool bIsView,
		bool bViewHasSizeBytes,
		int64 ViewSizeBytes,
		bool bListingHasSha256,
		const FString& ListingSha256,
		bool bListingHasSizeBytes,
		int64 ListingSizeBytes);

	static EMantlePlaceCacheState DeriveCacheState(bool bFileExists, const FMantlePlaceCacheValidity& Validity);

	static EMantlePlaceCacheState StateAtLevel(EMantlePlaceCacheState State, const FString& RecordedLevel, const FString& WantedLevel);

	static bool ParseExpiry(const FString& Iso8601, FDateTime& OutUtc);

	static bool IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc, int32 SkewSeconds = 60);

	static bool Sha256Equal(const FString& A, const FString& B);

	static FString SerializeMeta(const FMantlePlaceCachedBundle& Meta);

	static bool ParseMeta(const FString& Json, FMantlePlaceCachedBundle& Out, FString& OutError);
};
