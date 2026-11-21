// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTerrainTilesFetcher.h"

#include "MantlePlaceAuthSubsystem.h"
#include "MantlePlaceAuthSystemBase.h"
#include "MantlePlaceBundleCache.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceTerrainTilesLogic.h"
#include "MantlePlaceVaultClient.h"

#include "FileUtilities/ZipArchiveReader.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceTerrainTiles, Log, All);

namespace
{
	/** True when the file at Path is a readable zip holding a tileset (a `layer.json` inside a folder). */
	bool IsUsableTilesZip(const FString& Path)
	{
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		if (!PlatformFile.FileExists(*Path))
		{
			return false;
		}
		IFileHandle* Handle = PlatformFile.OpenRead(*Path);
		if (Handle == nullptr)
		{
			return false;
		}
		const FZipArchiveReader Reader(Handle); // takes ownership of the handle
		return Reader.IsValid() && FMantlePlaceTerrainTilesLogic::FindTileset(Reader.GetFileNames()).bFound;
	}
}

void UMantlePlaceTerrainTilesFetcher::EnsureClient()
{
	if (VaultClient != nullptr)
	{
		return;
	}
	// The editor's ONE auth session, as the vault panel uses it: signing in there signs this in.
	UMantlePlaceAuthSystemBase* AuthSystem = nullptr;
	if (UMantlePlaceAuthSubsystem* AuthSubsystem = UMantlePlaceAuthSubsystem::Get())
	{
		AuthSystem = AuthSubsystem->GetAuthSystem();
	}
	if (AuthSystem == nullptr)
	{
		AuthSystem = NewObject<UMantlePlaceAuthSystemBase>(this); // no editor: nothing is signed in, and the listing says so
	}
	VaultClient = NewObject<UMantlePlaceVaultClient>(this);
	VaultClient->OnVaultListedNative.AddUObject(this, &UMantlePlaceTerrainTilesFetcher::HandleListedNative);
	VaultClient->OnPresignedUrlReadyNative.AddUObject(this, &UMantlePlaceTerrainTilesFetcher::HandlePresignedNative);
	VaultClient->Initialize(AuthSystem);
}

bool UMantlePlaceTerrainTilesFetcher::Fetch(const FString& BundleZipPath)
{
	if (IsBusy())
	{
		UE_LOG(LogMantlePlaceTerrainTiles, Warning, TEXT("A terrain tiles fetch is already running; this ask was not started."));
		return false;
	}
	Step = EStep::Listing; // held from the first line, so a reuse or a refusal below settles through Finish like the rest

	FMantlePlaceVaultManifest Manifest;
	FString ReadError;
	if (!UMantlePlaceImporterLibrary::ReadVaultManifest(BundleZipPath, Manifest, ReadError))
	{
		Finish(false, ReadError);
		return true;
	}
	if (!Manifest.bFormatThree)
	{
		Finish(false, FMantlePlaceTerrainTilesLogic::NotThreeReason());
		return true;
	}

	ActiveBundleZip = BundleZipPath;
	ActiveOrderId = Manifest.OrderId;
	ActiveTilesPath = FMantlePlaceTerrainTilesLogic::TilesZipBeside(BundleZipPath);

	// Fetched once: tiles already beside the bundle are the answer, and nothing is requested.
	if (FMantlePlaceTerrainTilesLogic::PlanFetch(IsUsableTilesZip(ActiveTilesPath), nullptr)
		== FMantlePlaceTerrainTilesLogic::EFetchPlan::Reuse)
	{
		UE_LOG(LogMantlePlaceTerrainTiles, Log, TEXT("Reusing the terrain tiles already beside the bundle: %s"), *ActiveTilesPath);
		Finish(true, FString::Printf(TEXT("Terrain tiles already on disk: %s"), *ActiveTilesPath));
		return true;
	}

	// The plugin keeps the Extra in the folder of a bundle it downloaded. A zip from anywhere else has no such
	// folder of the plugin's, and nothing is written beside it.
	const FString CacheRoot = FPaths::Combine(FPaths::ProjectSavedDir(), GetDefault<UMantlePlaceBundleCache>()->CacheSubDir);
	if (!FMantlePlaceTerrainTilesLogic::IsInVaultCache(BundleZipPath, CacheRoot))
	{
		Finish(false, FMantlePlaceTerrainTilesLogic::NoTilesBesideReason());
		return true;
	}
	if (ActiveOrderId.IsEmpty())
	{
		Finish(false, TEXT("The bundle's manifest names no order, so the platform cannot be asked for its terrain tiles."));
		return true;
	}

	EnsureClient();
	VaultClient->ListVault(); // -> HandleListedNative; the client renews an expired token and replays on its own
	return true;
}

void UMantlePlaceTerrainTilesFetcher::HandleListedNative(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	if (Step != EStep::Listing)
	{
		return;
	}
	if (!bSuccess)
	{
		Finish(false, FString::Printf(TEXT("Could not list the vault to find the terrain tiles: %s"), *Message));
		return;
	}
	const FMantlePlaceVaultItem* Row = Bundles.FindByPredicate(
		[this](const FMantlePlaceVaultItem& Item) { return Item.OrderId == ActiveOrderId; });
	const FMantlePlaceTerrainTilesLogic::EFetchPlan Plan = FMantlePlaceTerrainTilesLogic::PlanFetch(/*bTilesBesideUsable*/ false, Row);
	if (Plan == FMantlePlaceTerrainTilesLogic::EFetchPlan::NoTilesListed)
	{
		Finish(false, FMantlePlaceTerrainTilesLogic::NoTilesListedReason());
		return;
	}
	if (Plan == FMantlePlaceTerrainTilesLogic::EFetchPlan::Locked)
	{
		// A free bundle: the platform would refuse the link, so none is asked for.
		Finish(false, FMantlePlaceTerrainTilesLogic::LockedReason());
		return;
	}
	ActiveExtra = *Row->FindExtra(MantlePlaceTerrainTilesExtraId());
	Step = EStep::Minting;
	VaultClient->GetPresignedExtraUrl(ActiveOrderId, ActiveExtra.Id);
}

void UMantlePlaceTerrainTilesFetcher::HandlePresignedNative(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message)
{
	if (Step != EStep::Minting)
	{
		return;
	}
	if (!bSuccess || Download.Url.IsEmpty())
	{
		// The platform refused the link as locked: the locked row's words, not "could not".
		Finish(false, Message == FMantlePlaceTerrainTilesLogic::LockedReason()
			? Message
			: FString::Printf(TEXT("Could not get a link for the terrain tiles: %s"), *Message));
		return;
	}
	Step = EStep::Downloading;

	// The minted link is storage, not the Platform API: it carries its own signature and no client header.
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest(); // not a Platform API call
	Request->SetVerb(TEXT("GET"));
	Request->SetURL(Download.Url);
	TWeakObjectPtr<UMantlePlaceTerrainTilesFetcher> WeakThis(this);
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bConnected)
		{
			if (UMantlePlaceTerrainTilesFetcher* Self = WeakThis.Get())
			{
				Self->HandleDownloaded(
					Response.IsValid() ? Response->GetContent() : TArray<uint8>(), Response.IsValid() ? Response->GetResponseCode() : 0, bConnected);
			}
		});
	if (!Request->ProcessRequest())
	{
		Finish(false, TEXT("Could not start the terrain tiles download."));
	}
}

void UMantlePlaceTerrainTilesFetcher::HandleDownloaded(const TArray<uint8>& Bytes, int32 ResponseCode, bool bConnected)
{
	if (Step != EStep::Downloading)
	{
		return;
	}
	if (!bConnected || !EHttpResponseCodes::IsOk(ResponseCode))
	{
		Finish(false, FString::Printf(TEXT("The terrain tiles download failed (HTTP %d)."), ResponseCode));
		return;
	}
	// Fail closed: the Extra states its length, and a download of another length is not that file.
	if (!FMantlePlaceTerrainTilesLogic::SizeMatches(ActiveExtra.SizeBytes, Bytes.Num()))
	{
		Finish(false, FString::Printf(TEXT("The terrain tiles download is %d bytes; the platform lists %lld. Nothing was kept."),
			Bytes.Num(), ActiveExtra.SizeBytes));
		return;
	}

	// Written beside, verified, then promoted by rename, so a torn download never looks like a cached one.
	const FString PartPath = ActiveTilesPath + TEXT(".part");
	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	PlatformFile.CreateDirectoryTree(*FPaths::GetPath(ActiveTilesPath));
	if (!FFileHelper::SaveArrayToFile(Bytes, *PartPath))
	{
		Finish(false, FString::Printf(TEXT("Could not write the terrain tiles to %s."), *PartPath));
		return;
	}
	if (!IsUsableTilesZip(PartPath))
	{
		IFileManager::Get().Delete(*PartPath);
		Finish(false, FMantlePlaceTerrainTilesLogic::NotATilesetReason());
		return;
	}
	if (!IFileManager::Get().Move(*ActiveTilesPath, *PartPath, /*Replace*/ true))
	{
		IFileManager::Get().Delete(*PartPath);
		Finish(false, FString::Printf(TEXT("Could not keep the terrain tiles at %s."), *ActiveTilesPath));
		return;
	}
	UE_LOG(LogMantlePlaceTerrainTiles, Log, TEXT("Fetched the terrain tiles (%d bytes) to %s."), Bytes.Num(), *ActiveTilesPath);
	Finish(true, FString::Printf(TEXT("Fetched the terrain tiles to %s."), *ActiveTilesPath));
}

void UMantlePlaceTerrainTilesFetcher::Finish(bool bSuccess, const FString& Message)
{
	const FString TilesPath = bSuccess ? ActiveTilesPath : FString();
	Step = EStep::Idle;
	ActiveExtra = FMantlePlaceVaultExtra();
	if (!bSuccess)
	{
		UE_LOG(LogMantlePlaceTerrainTiles, Warning, TEXT("%s"), *Message);
	}
	OnFetched.Broadcast(bSuccess, TilesPath, Message);
}

void UMantlePlaceTerrainTilesFetcher::BeginDestroy()
{
	if (VaultClient != nullptr)
	{
		VaultClient->OnVaultListedNative.RemoveAll(this);
		VaultClient->OnPresignedUrlReadyNative.RemoveAll(this);
	}
	Super::BeginDestroy();
}
