// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceBundleCacheLogic.h"
#include "MantlePlaceSha256.h"

#include "Containers/StringConv.h"
#include "Dom/JsonObject.h"
#include "Misc/DateTime.h"
#include "Misc/Paths.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonWriter.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	const TCHAR* const BundleFileName = TEXT("bundle.zip");
	const TCHAR* const PartFileName = TEXT("bundle.zip.part");
	const TCHAR* const MetaFileName = TEXT("cache.json");

	FString BundleCacheSerializeCondensed(const TSharedRef<FJsonObject>& Root)
	{
		FString Out;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	TSharedPtr<FJsonObject> BundleCacheDeserializeObject(const FString& JsonStr)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
		if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
		{
			return Root;
		}
		return nullptr;
	}
}

FString FMantlePlaceBundleCacheLogic::SanitizeKeySegment(const FString& OrderId)
{
	FString Out;
	Out.Reserve(OrderId.Len());
	const int32 Length = OrderId.Len();
	for (int32 Index = 0; Index < Length; ++Index)
	{
		uint32 CodePoint = static_cast<uint32>(OrderId[Index]);
		if (StringConv::IsHighSurrogate(CodePoint) && Index + 1 < Length
			&& StringConv::IsLowSurrogate(static_cast<uint32>(OrderId[Index + 1])))
		{
			CodePoint = StringConv::EncodeSurrogate(
				static_cast<uint16>(CodePoint), static_cast<uint16>(OrderId[Index + 1]));
			++Index;
		}

		const bool bIsBmpCodePoint = CodePoint <= 0xFFFF
			&& !StringConv::IsHighSurrogate(CodePoint) && !StringConv::IsLowSurrogate(CodePoint);
		const TCHAR C = static_cast<TCHAR>(CodePoint);
		const bool bKeep = bIsBmpCodePoint
			&& (FChar::IsAlnum(C) || C == TEXT('.') || C == TEXT('_') || C == TEXT('-'));
		Out.AppendChar(bKeep ? C : TEXT('_'));
	}
	if (Out.IsEmpty() || Out == TEXT(".") || Out == TEXT(".."))
	{
		Out = TEXT("_");
	}
	return Out;
}

FString FMantlePlaceBundleCacheLogic::DeriveBundleDir(const FString& CacheRoot, const FString& OrderId)
{
	const FString Sanitized = SanitizeKeySegment(OrderId);
	if (Sanitized != OrderId)
	{
		const FTCHARToUTF8 Utf8(*OrderId);
		const FString Hash = FMantlePlaceSha256::HexDigest(
			TConstArrayView<uint8>(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length()));
		return FPaths::Combine(CacheRoot, Sanitized + TEXT("_") + Hash.Left(8));
	}
	return FPaths::Combine(CacheRoot, Sanitized);
}

FString FMantlePlaceBundleCacheLogic::DeriveBundlePath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), BundleFileName);
}

FString FMantlePlaceBundleCacheLogic::DerivePartPath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), PartFileName);
}

FString FMantlePlaceBundleCacheLogic::DeriveMetaPath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), MetaFileName);
}

FMantlePlaceCacheValidity FMantlePlaceBundleCacheLogic::DecideValidity(
	bool bFileExists,
	int64 OnDiskSizeBytes,
	const FString& ComputedSha256,
	bool bHasExpectedSha,
	const FString& ExpectedSha256,
	bool bHasExpectedSize,
	int64 ExpectedSizeBytes,
	bool bHasManifestVersion,
	const FString& ManifestVersion,
	const FString& MinVersion)
{
	FMantlePlaceCacheValidity Validity;

	if (!bFileExists)
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::Missing;
		Validity.bIntegrityChecked = false;
		return Validity;
	}

	if (bHasExpectedSize && OnDiskSizeBytes != ExpectedSizeBytes)
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::SizeMismatch;
		Validity.bIntegrityChecked = false;
		return Validity;
	}

	const bool bDidHash = bHasExpectedSha && !ComputedSha256.IsEmpty();
	if (bDidHash && !Sha256Equal(ComputedSha256, ExpectedSha256))
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::Sha256Mismatch;
		Validity.bIntegrityChecked = true;
		return Validity;
	}

	if (bHasManifestVersion && MantlePlaceIsManifestVersionBelowFloor(ManifestVersion, MinVersion))
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::ManifestTooOld;
		Validity.bIntegrityChecked = bDidHash;
		return Validity;
	}

	Validity.bValid = true;
	Validity.Reason = EMantlePlaceCacheInvalidReason::None;
	Validity.bIntegrityChecked = bDidHash;
	return Validity;
}

EMantlePlaceCacheState FMantlePlaceBundleCacheLogic::DeriveCacheState(
	bool bFileExists, const FMantlePlaceCacheValidity& Validity)
{
	if (!bFileExists)
	{
		return EMantlePlaceCacheState::NotCached;
	}
	return Validity.bValid ? EMantlePlaceCacheState::CachedValid : EMantlePlaceCacheState::CachedStale;
}

EMantlePlaceCacheState FMantlePlaceBundleCacheLogic::StateAtLevel(
	EMantlePlaceCacheState State, const FString& RecordedLevel, const FString& WantedLevel)
{
	if (State == EMantlePlaceCacheState::CachedValid && !RecordedLevel.Equals(WantedLevel, ESearchCase::CaseSensitive))
	{
		return EMantlePlaceCacheState::CachedStale;
	}
	return State;
}

bool FMantlePlaceBundleCacheLogic::ParseExpiry(const FString& Iso8601, FDateTime& OutUtc)
{
	if (Iso8601.IsEmpty())
	{
		return false;
	}
	return FDateTime::ParseIso8601(*Iso8601, OutUtc);
}

bool FMantlePlaceBundleCacheLogic::IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc, int32 SkewSeconds)
{
	return (NowUtc + FTimespan::FromSeconds(SkewSeconds)) >= ExpiresAtUtc;
}

bool FMantlePlaceBundleCacheLogic::Sha256Equal(const FString& A, const FString& B)
{
	return A.TrimStartAndEnd().Equals(B.TrimStartAndEnd(), ESearchCase::IgnoreCase);
}

FMantlePlaceCacheExpectation FMantlePlaceBundleCacheLogic::ExpectationForHeld(
	bool bHoldsView,
	const FString& RecordedSha256,
	int64 RecordedSizeBytes,
	bool bListingHasSha256,
	const FString& ListingSha256,
	bool bListingHasSizeBytes,
	int64 ListingSizeBytes)
{
	FMantlePlaceCacheExpectation Expectation;
	if (bHoldsView)
	{
		Expectation.bHasSha256 = !RecordedSha256.IsEmpty();
		Expectation.Sha256 = RecordedSha256;
		Expectation.bHasSizeBytes = RecordedSizeBytes > 0;
		Expectation.SizeBytes = RecordedSizeBytes;
		return Expectation;
	}
	Expectation.bHasSha256 = bListingHasSha256;
	Expectation.Sha256 = ListingSha256;
	Expectation.bHasSizeBytes = bListingHasSizeBytes;
	Expectation.SizeBytes = ListingSizeBytes;
	return Expectation;
}

FMantlePlaceCacheExpectation FMantlePlaceBundleCacheLogic::ExpectationForDownload(
	bool bIsView,
	bool bViewHasSizeBytes,
	int64 ViewSizeBytes,
	bool bListingHasSha256,
	const FString& ListingSha256,
	bool bListingHasSizeBytes,
	int64 ListingSizeBytes)
{
	FMantlePlaceCacheExpectation Expectation;
	if (bIsView)
	{
		Expectation.bHasSizeBytes = bViewHasSizeBytes;
		Expectation.SizeBytes = ViewSizeBytes;
		return Expectation;
	}
	Expectation.bHasSha256 = bListingHasSha256;
	Expectation.Sha256 = ListingSha256;
	Expectation.bHasSizeBytes = bListingHasSizeBytes;
	Expectation.SizeBytes = ListingSizeBytes;
	return Expectation;
}

bool FMantlePlaceBundleCacheLogic::IsViewRecut(
	bool bHoldsView, const FString& RecordedListingSha256, bool bListingHasSha256, const FString& ListingSha256)
{
	return bHoldsView && bListingHasSha256 && !RecordedListingSha256.IsEmpty()
		&& !Sha256Equal(RecordedListingSha256, ListingSha256);
}

FString FMantlePlaceBundleCacheLogic::SerializeMeta(const FMantlePlaceCachedBundle& Meta)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("orderId"), Meta.OrderId);
	Root->SetStringField(TEXT("localPath"), Meta.LocalPath);
	Root->SetStringField(TEXT("sha256"), Meta.Sha256);
	Root->SetNumberField(TEXT("sizeBytes"), static_cast<double>(Meta.SizeBytes));
	Root->SetStringField(TEXT("manifestVersion"), Meta.ManifestVersion);
	Root->SetStringField(TEXT("downloadedAtUtc"), Meta.DownloadedAtUtc);
	Root->SetStringField(TEXT("format"), Meta.Format);
	Root->SetBoolField(TEXT("holdsView"), Meta.bHoldsView);
	Root->SetStringField(TEXT("listingSha256"), Meta.ListingSha256);
	if (!Meta.Level.IsEmpty())
	{
		Root->SetStringField(TEXT("level"), Meta.Level);
	}
	return BundleCacheSerializeCondensed(Root);
}

bool FMantlePlaceBundleCacheLogic::ParseMeta(const FString& Json, FMantlePlaceCachedBundle& Out, FString& OutError)
{
	const TSharedPtr<FJsonObject> Root = BundleCacheDeserializeObject(Json);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in cache sidecar");
		return false;
	}

	FMantlePlaceCachedBundle Parsed;
	if (!Root->TryGetStringField(TEXT("orderId"), Parsed.OrderId) || Parsed.OrderId.IsEmpty())
	{
		OutError = TEXT("Cache sidecar missing required 'orderId'");
		return false;
	}

	Root->TryGetStringField(TEXT("localPath"), Parsed.LocalPath);
	Root->TryGetStringField(TEXT("sha256"), Parsed.Sha256);

	double SizeBytes = 0.0;
	if (Root->TryGetNumberField(TEXT("sizeBytes"), SizeBytes))
	{
		Parsed.SizeBytes = static_cast<int64>(SizeBytes);
	}

	if (!Root->TryGetStringField(TEXT("manifestVersion"), Parsed.ManifestVersion))
	{
		double LegacyManifestVersion = 0.0;
		if (Root->TryGetNumberField(TEXT("manifestVersion"), LegacyManifestVersion))
		{
			Parsed.ManifestVersion = FString::FromInt(static_cast<int32>(LegacyManifestVersion));
		}
	}

	Root->TryGetStringField(TEXT("downloadedAtUtc"), Parsed.DownloadedAtUtc);
	Root->TryGetStringField(TEXT("format"), Parsed.Format);
	Root->TryGetBoolField(TEXT("holdsView"), Parsed.bHoldsView);
	Root->TryGetStringField(TEXT("listingSha256"), Parsed.ListingSha256);
	Root->TryGetStringField(TEXT("level"), Parsed.Level);

	Out = MoveTemp(Parsed);
	return true;
}
