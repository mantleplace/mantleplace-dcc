// Copyright Mantle Place. All Rights Reserved.

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

FMantlePlaceCachedBundle UMantlePlaceBundleCache::ResolveCached(const FMantlePlaceVaultItem& Item) const
{
	const FString Root = GetCacheRoot();
	IFileManager& Files = IFileManager::Get();

	FMantlePlaceCachedBundle Cached;
	Cached.OrderId = Item.OrderId;
	Cached.LocalPath = FCacheLogic::DeriveBundlePath(Root, Item.OrderId);

	const bool bExists = Files.FileExists(*Cached.LocalPath);
	const int64 Size = bExists ? Files.FileSize(*Cached.LocalPath) : -1;
	Cached.SizeBytes = (Size > 0) ? Size : 0;

	// Prefer the sha recorded in the sidecar at download time - validating a list refresh by
	// re-hashing a multi-GB file on disk would block the editor. The sidecar sha is what we
	// downloaded; comparing it to the vault's current sha detects a re-cut (-> CachedStale).
	FString RecordedSha;
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
				Cached.ManifestVersion = Meta.ManifestVersion;
				Cached.DownloadedAtUtc = Meta.DownloadedAtUtc;
				Cached.Format = Meta.Format;
			}
		}
	}
	Cached.Sha256 = RecordedSha;

	const FMantlePlaceCacheValidity Validity = FCacheLogic::DecideValidity(
		bExists, Size, RecordedSha,
		Item.bHasSha256, Item.Sha256,
		Item.bHasSizeBytes, Item.SizeBytes,
		Item.bHasManifestVersion, Item.ManifestVersion);
	Cached.State = FCacheLogic::DeriveCacheState(bExists, Validity);
	return Cached;
}

FMantlePlaceCachedBundle UMantlePlaceBundleCache::InspectCache(const FMantlePlaceVaultItem& Item)
{
	const FMantlePlaceCachedBundle Cached = ResolveCached(Item);
	OnCacheStateResolved(Item, Cached);
	return Cached;
}

bool UMantlePlaceBundleCache::IsCachedAndValid(const FMantlePlaceVaultItem& Item) const
{
	return ResolveCached(Item).State == EMantlePlaceCacheState::CachedValid;
}

bool UMantlePlaceBundleCache::EvictCache(const FMantlePlaceVaultItem& Item)
{
	if (Item.OrderId.IsEmpty())
	{
		return false;
	}
	const FString Dir = FCacheLogic::DeriveBundleDir(GetCacheRoot(), Item.OrderId);
	return IFileManager::Get().DeleteDirectory(*Dir, /*RequireExists*/ false, /*Tree*/ true);
}

void UMantlePlaceBundleCache::DownloadBundle(
	const FMantlePlaceVaultItem& Item, const FMantlePlacePresignedDownload& Presigned)
{
	if (Item.OrderId.IsEmpty())
	{
		NotifyDownloadComplete(false, FString(), TEXT("OrderId is required."));
		return;
	}

	// Offline re-import: a valid cached bundle is served without any network round-trip (the
	// anti-streaming guarantee made literal). Checked before the URL so it works fully offline.
	const FMantlePlaceCachedBundle Cached = ResolveCached(Item);
	if (Cached.State == EMantlePlaceCacheState::CachedValid)
	{
		NotifyDownloadComplete(true, Cached.LocalPath, TEXT("Cache hit - re-importing the owned local bundle."));
		return;
	}

	if (Presigned.Url.IsEmpty())
	{
		NotifyDownloadComplete(false, FString(), TEXT("No presigned URL to download."));
		return;
	}

	// A presigned URL is short-lived; refuse a stale one with guidance rather than 403-failing mid-stream.
	FDateTime ExpiresUtc;
	if (FCacheLogic::ParseExpiry(Presigned.ExpiresAt, ExpiresUtc) && FCacheLogic::IsExpired(FDateTime::UtcNow(), ExpiresUtc))
	{
		NotifyDownloadComplete(false, FString(), TEXT("Presigned URL has expired - re-mint it and retry."));
		return;
	}

	// Single in-flight download (mirrors the vault client's single-request discipline).
	if (ActiveRequest.IsValid())
	{
		NotifyDownloadComplete(false, FString(), TEXT("A download is already in progress."));
		return;
	}

	const FString Root = GetCacheRoot();
	ActiveItem = Item;
	ActiveFinalPath = FCacheLogic::DeriveBundlePath(Root, Item.OrderId);
	ActivePartPath = FCacheLogic::DerivePartPath(Root, Item.OrderId);
	ExpectedTotalBytes = Item.bHasSizeBytes ? static_cast<uint64>(Item.SizeBytes) : 0;

	IFileManager& Files = IFileManager::Get();
	Files.MakeDirectory(*FCacheLogic::DeriveBundleDir(Root, Item.OrderId), /*Tree*/ true);
	Files.Delete(*ActivePartPath, /*RequireExists*/ false, /*EvenIfReadOnly*/ true, /*Quiet*/ true);

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
	// Stream the body straight to the .part file (no full in-memory buffer); complete + progress on
	// the game thread so the Blueprint events are safe to fire directly (the body Serialize runs on
	// the HTTP thread, which the FArchive owns).
	ActiveRequest->SetDelegateThreadPolicy(EHttpRequestDelegateThreadPolicy::CompleteOnGameThread);
	if (!ActiveRequest->SetResponseBodyReceiveStream(ResponseFileWriter.ToSharedRef()))
	{
		FinishStream(/*bDeletePartFile*/ true);
		NotifyDownloadComplete(false, FString(), TEXT("This HTTP backend cannot stream the response to disk."));
		return;
	}

	TWeakObjectPtr<UMantlePlaceBundleCache> WeakThis(this);
	ActiveRequest->OnRequestProgress64().BindLambda(
		[WeakThis](FHttpRequestPtr /*Request*/, uint64 /*BytesSent*/, uint64 BytesReceived)
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
		FinishStream(/*bDeletePartFile*/ true);
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
	TSharedPtr<IHttpRequest, ESPMode::ThreadSafe> /*Request*/,
	TSharedPtr<IHttpResponse, ESPMode::ThreadSafe> Response,
	bool bConnectedSuccessfully)
{
	// We are inside the completion delegate, so do NOT unbind/cancel it (that is the start/cancel/
	// destroy path). Just close the streamed writer so the .part is flushed + the OS handle released
	// before we read it back to hash, and drop our request reference.
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

	const FMantlePlaceCacheValidity Validity = FCacheLogic::DecideValidity(
		/*bFileExists*/ OnDiskSize >= 0, OnDiskSize, ComputedSha,
		ActiveItem.bHasSha256, ActiveItem.Sha256,
		ActiveItem.bHasSizeBytes, ActiveItem.SizeBytes,
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

	// Promote the verified .part to the canonical bundle.zip atomically.
	IFileManager& Files = IFileManager::Get();
	if (!Files.Move(*ActiveFinalPath, *ActivePartPath, /*bReplace*/ true, /*bEvenIfReadOnly*/ true))
	{
		FailDownload(TEXT("Could not finalize the cached bundle."));
		return;
	}

	// Record the cache sidecar (integrity facts; the list state is recomputed at inspect time).
	FMantlePlaceCachedBundle Meta;
	Meta.OrderId = ActiveItem.OrderId;
	Meta.LocalPath = ActiveFinalPath;
	Meta.Sha256 = ComputedSha;
	Meta.SizeBytes = OnDiskSize;
	Meta.ManifestVersion = ActiveItem.bHasManifestVersion ? ActiveItem.ManifestVersion : 0;
	Meta.DownloadedAtUtc = FDateTime::UtcNow().ToIso8601();
	Meta.Format = TEXT("glb");
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
		return FString(); // missing
	}
	if (OutSizeBytes > MaxHashSizeBytes)
	{
		// Too big to hash synchronously on the game thread - validity falls back to size (+ version).
		return FString();
	}

	// Stream the file through the incremental hasher in fixed chunks - no whole-file allocation
	// (the size cap above still bounds how long this synchronous hash runs on the game thread).
	TUniquePtr<FArchive> Reader(IFileManager::Get().CreateFileReader(*Path));
	if (!Reader)
	{
		return FString();
	}

	FMantlePlaceSha256 Hasher;
	static constexpr int64 ChunkSize = 1 << 20; // 1 MiB
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
		IFileManager::Get().Delete(*ActivePartPath, /*RequireExists*/ false, /*EvenIfReadOnly*/ true, /*Quiet*/ true);
	}
}

void UMantlePlaceBundleCache::FailDownload(const FString& Message)
{
	if (!ActivePartPath.IsEmpty())
	{
		IFileManager::Get().Delete(*ActivePartPath, /*RequireExists*/ false, /*EvenIfReadOnly*/ true, /*Quiet*/ true);
	}
	ActivePartPath.Reset();
	ActiveFinalPath.Reset();
	UE_LOG(LogMantlePlaceCache, Warning, TEXT("Download failed: %s"), *Message);
	NotifyDownloadComplete(false, FString(), Message);
}

void UMantlePlaceBundleCache::CancelDownload()
{
	// User-cancelled: tear down the request + partial file, but fire no completion event.
	FinishStream(/*bDeletePartFile*/ true);
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
	FinishStream(/*bDeletePartFile*/ false);
	Super::BeginDestroy();
}
