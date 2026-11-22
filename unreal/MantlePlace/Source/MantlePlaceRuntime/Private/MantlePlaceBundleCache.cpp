// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceBundleCache.h"

#include "MantlePlaceBundleCacheLogic.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "HAL/FileManager.h"
#include "Misc/DateTime.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceCache, Log, All);

using FCacheLogic = FMantlePlaceBundleCacheLogic;

FString UMantlePlaceBundleCache::GetCacheRoot() const
{
	const FString Sub = CacheSubDir.IsEmpty() ? TEXT("MantlePlace/VaultCache") : CacheSubDir;
	return FPaths::Combine(FPaths::ProjectSavedDir(), Sub);
}

FString UMantlePlaceBundleCache::GetCachedBundlePath(const FMantlePlaceVaultItem& Item) const
{
	return FCacheLogic::DeriveBundlePath(GetCacheRoot(), Item.OrderId);
}

FMantlePlaceCachedBundle UMantlePlaceBundleCache::ResolveCached(const FMantlePlaceVaultItem& Item, const FString* WantedLevel) const
{
	const FString Root = GetCacheRoot();
	IFileManager& Files = IFileManager::Get();

	FMantlePlaceCachedBundle Cached;
	Cached.OrderId = Item.OrderId;
	Cached.LocalPath = FCacheLogic::DeriveBundlePath(Root, Item.OrderId);

	const bool bExists = Files.FileExists(*Cached.LocalPath);
	const int64 Size = bExists ? Files.FileSize(*Cached.LocalPath) : -1;
	Cached.SizeBytes = (Size > 0) ? Size : 0;

	FString RecordedSha;
	FString RecordedListingSha;
	int64 RecordedSize = 0;
	bool bHoldsView = false;
	if (bExists)
	{
		FString MetaJson;
		if (FFileHelper::LoadFileToString(MetaJson, *FCacheLogic::DeriveMetaPath(Root, Item.OrderId)))
		{
			FMantlePlaceCachedBundle Meta;
			FString MetaError;
			if (FCacheLogic::ParseMeta(MetaJson, Meta, MetaError))
			{
				RecordedSha = Meta.Sha256;
				bHoldsView = Meta.bHoldsView;
				RecordedSize = Meta.SizeBytes;
				RecordedListingSha = Meta.ListingSha256;
				Cached.bHoldsView = Meta.bHoldsView;
				Cached.ManifestVersion = Meta.ManifestVersion;
				Cached.DownloadedAtUtc = Meta.DownloadedAtUtc;
				Cached.Format = Meta.Format;
				Cached.Level = Meta.Level;
			}
		}
	}
	Cached.Sha256 = RecordedSha;

	const FMantlePlaceCacheExpectation Expected = FCacheLogic::ExpectationForHeld(
		bHoldsView, RecordedSha, RecordedSize,
		Item.bHasSha256, Item.Sha256, Item.bHasSizeBytes, Item.SizeBytes);
	const FMantlePlaceCacheValidity Validity = FCacheLogic::DecideValidity(
		bExists, Size, RecordedSha,
		Expected.bHasSha256, Expected.Sha256,
		Expected.bHasSizeBytes, Expected.SizeBytes,
		Item.bHasManifestVersion, Item.ManifestVersion);
	Cached.State = FCacheLogic::DeriveCacheState(bExists, Validity);
	if (Cached.State == EMantlePlaceCacheState::CachedValid
		&& FCacheLogic::IsViewRecut(bHoldsView, RecordedListingSha, Item.bHasSha256, Item.Sha256))
	{
		Cached.State = EMantlePlaceCacheState::CachedStale;
	}
	if (WantedLevel != nullptr)
	{
		Cached.State = FCacheLogic::StateAtLevel(Cached.State, Cached.Level, *WantedLevel);
	}
	return Cached;
}

FMantlePlaceCachedBundle UMantlePlaceBundleCache::InspectCache(const FMantlePlaceVaultItem& Item)
{
	const FMantlePlaceCachedBundle Cached = ResolveCached(Item);
	OnCacheStateResolved(Item, Cached);
	return Cached;
}

bool UMantlePlaceBundleCache::CachedHoldsView(const FMantlePlaceVaultItem& Item) const
{
	return ResolveCached(Item).bHoldsView;
}

bool UMantlePlaceBundleCache::IsCachedAndValid(const FMantlePlaceVaultItem& Item) const
{
	return ResolveCached(Item).State == EMantlePlaceCacheState::CachedValid;
}

bool UMantlePlaceBundleCache::IsCachedAndValidAtLevel(const FMantlePlaceVaultItem& Item, const FString& Level) const
{
	return ResolveCached(Item, &Level).State == EMantlePlaceCacheState::CachedValid;
}

bool UMantlePlaceBundleCache::EvictCache(const FMantlePlaceVaultItem& Item)
{
	if (Item.OrderId.IsEmpty())
	{
		return false;
	}
	const FString Dir = FCacheLogic::DeriveBundleDir(GetCacheRoot(), Item.OrderId);
	return IFileManager::Get().DeleteDirectory(*Dir, false, true);
}

void UMantlePlaceBundleCache::DownloadBundle(
	const FMantlePlaceVaultItem& Item, const FMantlePlacePresignedDownload& Presigned)
{
	if (Item.OrderId.IsEmpty())
	{
		NotifyDownloadComplete(false, FString(), TEXT("OrderId is required."));
		return;
	}

	const FMantlePlaceCachedBundle Cached = ResolveCached(Item, &Presigned.Level);
	if (Cached.State == EMantlePlaceCacheState::CachedValid)
	{
		NotifyDownloadComplete(true, Cached.LocalPath, TEXT("Cache hit - re-importing the kept local bundle."));
		return;
	}

	if (Presigned.Url.IsEmpty())
	{
		NotifyDownloadComplete(false, FString(), TEXT("No presigned URL to download."));
		return;
	}

	FDateTime ExpiresUtc;
	if (FCacheLogic::ParseExpiry(Presigned.ExpiresAt, ExpiresUtc) && FCacheLogic::IsExpired(FDateTime::UtcNow(), ExpiresUtc))
	{
		NotifyDownloadComplete(false, FString(), TEXT("Presigned URL has expired - re-mint it and retry."));
		return;
	}

	if (ActiveRequest.IsValid())
	{
		NotifyDownloadComplete(false, FString(), TEXT("A download is already in progress."));
		return;
	}

	const FString Root = GetCacheRoot();
	ActiveItem = Item;
	ActiveFinalPath = FCacheLogic::DeriveBundlePath(Root, Item.OrderId);
	ActivePartPath = FCacheLogic::DerivePartPath(Root, Item.OrderId);
	bActiveIsView = Presigned.bIsView;
	ActiveViewSizeBytes = Presigned.bHasSizeBytes ? Presigned.SizeBytes : 0;
	ActiveLevel = Presigned.Level;
	ExpectedTotalBytes = bActiveIsView
		? static_cast<uint64>(FMath::Max<int64>(0, ActiveViewSizeBytes))
		: (Item.bHasSizeBytes ? static_cast<uint64>(Item.SizeBytes) : 0);

	IFileManager& Files = IFileManager::Get();
	Files.MakeDirectory(*FCacheLogic::DeriveBundleDir(Root, Item.OrderId), true);
	Files.Delete(*ActivePartPath, false, true, true);

	FArchive* Writer = Files.CreateFileWriter(*ActivePartPath);
	if (Writer == nullptr)
	{
		NotifyDownloadComplete(false, FString(), TEXT("Could not open the cache file for writing."));
		return;
	}
	ResponseFileWriter = MakeShareable(Writer);

	ActiveRequest = FHttpModule::Get().CreateRequest();
	ActiveRequest->SetVerb(TEXT("GET"));
	ActiveRequest->SetURL(Presigned.Url);
	ActiveRequest->SetDelegateThreadPolicy(EHttpRequestDelegateThreadPolicy::CompleteOnGameThread);
	if (!ActiveRequest->SetResponseBodyReceiveStream(ResponseFileWriter.ToSharedRef()))
	{
		FinishStream(true);
		NotifyDownloadComplete(false, FString(), TEXT("This HTTP backend cannot stream the response to disk."));
		return;
	}

	TWeakObjectPtr<UMantlePlaceBundleCache> WeakThis(this);
	ActiveRequest->OnRequestProgress64().BindLambda(
		[WeakThis](FHttpRequestPtr, uint64, uint64 BytesReceived)
		{
			if (UMantlePlaceBundleCache* Self = WeakThis.Get())
			{
				Self->HandleDownloadProgress(BytesReceived);
			}
		});
	ActiveRequest->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr Request, FHttpResponsePtr Response, bool bConnectedSuccessfully)
		{
			if (UMantlePlaceBundleCache* Self = WeakThis.Get())
			{
				Self->HandleDownloadComplete(Request, Response, bConnectedSuccessfully);
			}
		});

	if (!ActiveRequest->ProcessRequest())
	{
		FinishStream(true);
		NotifyDownloadComplete(false, FString(), TEXT("Failed to start the download request."));
	}
}

void UMantlePlaceBundleCache::HandleDownloadProgress(uint64 BytesReceived)
{
	FMantlePlaceDownloadProgress Progress;
	Progress.BytesReceived = static_cast<int64>(BytesReceived);
	Progress.TotalBytes = static_cast<int64>(ExpectedTotalBytes);
	Progress.Fraction = (ExpectedTotalBytes > 0)
		? FMath::Clamp(static_cast<float>(static_cast<double>(BytesReceived) / static_cast<double>(ExpectedTotalBytes)), 0.0f, 1.0f)
		: -1.0f;
	NotifyDownloadProgress(Progress);
}

void UMantlePlaceBundleCache::HandleDownloadComplete(
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe>,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	if (ResponseFileWriter.IsValid())
	{
		ResponseFileWriter->Flush();
		ResponseFileWriter->Close();
		ResponseFileWriter.Reset();
	}
	ActiveRequest.Reset();

	if (!bConnectedSuccessfully || !Response.IsValid())
	{
		FailDownload(TEXT("Network error during download."));
		return;
	}

	const int32 ResponseCode = Response->GetResponseCode();
	if (!EHttpResponseCodes::IsOk(ResponseCode))
	{
		FailDownload(FString::Printf(TEXT("Download failed: HTTP %d."), ResponseCode));
		return;
	}

	int64 OnDiskSize = -1;
	const FString ComputedSha = ComputeFileSha256(ActivePartPath, OnDiskSize);

	const FMantlePlaceCacheExpectation Expected = FCacheLogic::ExpectationForDownload(
		bActiveIsView, bActiveIsView && ActiveViewSizeBytes > 0, ActiveViewSizeBytes,
		ActiveItem.bHasSha256, ActiveItem.Sha256, ActiveItem.bHasSizeBytes, ActiveItem.SizeBytes);
	const FMantlePlaceCacheValidity Validity = FCacheLogic::DecideValidity(
		OnDiskSize >= 0, OnDiskSize, ComputedSha,
		Expected.bHasSha256, Expected.Sha256,
		Expected.bHasSizeBytes, Expected.SizeBytes,
		ActiveItem.bHasManifestVersion, ActiveItem.ManifestVersion);

	if (!Validity.bValid)
	{
		const TCHAR* Reason =
			Validity.Reason == EMantlePlaceCacheInvalidReason::SizeMismatch ? TEXT("size mismatch") :
			Validity.Reason == EMantlePlaceCacheInvalidReason::Sha256Mismatch ? TEXT("checksum mismatch") :
			Validity.Reason == EMantlePlaceCacheInvalidReason::ManifestTooOld ? TEXT("manifest too old") : TEXT("missing");
		FailDownload(FString::Printf(TEXT("Downloaded bundle failed integrity (%s)."), Reason));
		return;
	}

	IFileManager& Files = IFileManager::Get();
	if (!Files.Move(*ActiveFinalPath, *ActivePartPath, true, true))
	{
		FailDownload(TEXT("Could not finalize the cached bundle."));
		return;
	}

	FMantlePlaceCachedBundle Meta;
	Meta.OrderId = ActiveItem.OrderId;
	Meta.LocalPath = ActiveFinalPath;
	Meta.Sha256 = ComputedSha;
	Meta.SizeBytes = OnDiskSize;
	Meta.ManifestVersion = ActiveItem.bHasManifestVersion ? ActiveItem.ManifestVersion : FString();
	Meta.DownloadedAtUtc = FDateTime::UtcNow().ToIso8601();
	Meta.Format = TEXT("glb");
	Meta.bHoldsView = bActiveIsView;
	Meta.ListingSha256 = ActiveItem.bHasSha256 ? ActiveItem.Sha256 : FString();
	Meta.Level = ActiveLevel;
	FFileHelper::SaveStringToFile(
		FCacheLogic::SerializeMeta(Meta), *FCacheLogic::DeriveMetaPath(GetCacheRoot(), ActiveItem.OrderId));

	UE_LOG(LogMantlePlaceCache, Log, TEXT("Cached bundle %s (%lld bytes, %s)."),
		*ActiveItem.OrderId, OnDiskSize, Validity.bIntegrityChecked ? TEXT("verified") : TEXT("size-verified"));

	const FString Final = ActiveFinalPath;
	ActivePartPath.Reset();
	ActiveFinalPath.Reset();
	NotifyDownloadComplete(true, Final,
		Validity.bIntegrityChecked ? TEXT("Downloaded and verified.") : TEXT("Downloaded (size-verified)."));
}

FString UMantlePlaceBundleCache::ComputeFileSha256(const FString& Path, int64& OutSizeBytes) const
{
	IFileManager& Files = IFileManager::Get();
	OutSizeBytes = Files.FileSize(*Path);
	if (OutSizeBytes < 0)
	{
		return FString();
	}
	if (OutSizeBytes > MaxHashSizeBytes)
	{
		return FString();
	}

	TUniquePtr<FArchive> Reader(IFileManager::Get().CreateFileReader(*Path));
	if (!Reader)
	{
		return FString();
	}

	FMantlePlaceSha256 Hasher;
	static constexpr int64 ChunkSize = 1 << 20;
	TArray<uint8> Chunk;
	Chunk.SetNumUninitialized(static_cast<int32>(ChunkSize));
	int64 Remaining = OutSizeBytes;
	while (Remaining > 0)
	{
		const int64 ThisChunk = FMath::Min(Remaining, ChunkSize);
		Reader->Serialize(Chunk.GetData(), ThisChunk);
		Hasher.Update(Chunk.GetData(), ThisChunk);
		Remaining -= ThisChunk;
	}
	Reader->Close();
	return Hasher.Final();
}

void UMantlePlaceBundleCache::FinishStream(bool bDeletePartFile)
{
	if (ResponseFileWriter.IsValid())
	{
		ResponseFileWriter->Flush();
		ResponseFileWriter->Close();
		ResponseFileWriter.Reset();
	}
	if (ActiveRequest.IsValid())
	{
		ActiveRequest->OnProcessRequestComplete().Unbind();
		ActiveRequest->OnRequestProgress64().Unbind();
		if (ActiveRequest->GetStatus() == EHttpRequestStatus::Processing)
		{
			ActiveRequest->CancelRequest();
		}
		ActiveRequest.Reset();
	}
	if (bDeletePartFile && !ActivePartPath.IsEmpty())
	{
		IFileManager::Get().Delete(*ActivePartPath, false, true, true);
	}
}

void UMantlePlaceBundleCache::FailDownload(const FString& Message)
{
	if (!ActivePartPath.IsEmpty())
	{
		IFileManager::Get().Delete(*ActivePartPath, false, true, true);
	}
	ActivePartPath.Reset();
	ActiveFinalPath.Reset();
	UE_LOG(LogMantlePlaceCache, Warning, TEXT("Download failed: %s"), *Message);
	NotifyDownloadComplete(false, FString(), Message);
}

void UMantlePlaceBundleCache::CancelDownload()
{
	FinishStream(true);
	ActivePartPath.Reset();
	ActiveFinalPath.Reset();
}

void UMantlePlaceBundleCache::NotifyDownloadComplete(bool bSuccess, const FString& LocalBundlePath, const FString& Message)
{
	OnDownloadCompleteNative.Broadcast(bSuccess, LocalBundlePath, Message);
	OnDownloadComplete(bSuccess, LocalBundlePath, Message);
}

void UMantlePlaceBundleCache::NotifyDownloadProgress(const FMantlePlaceDownloadProgress& Progress)
{
	OnDownloadProgressNative.Broadcast(Progress);
	OnDownloadProgress(Progress);
}

void UMantlePlaceBundleCache::BeginDestroy()
{
	FinishStream(false);
	Super::BeginDestroy();
}
