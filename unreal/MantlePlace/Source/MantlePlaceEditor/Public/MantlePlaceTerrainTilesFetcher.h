// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceTerrainTilesFetcher.generated.h"

class UMantlePlaceVaultClient;

/** The fetch settled: on success TilesZipPath is the Extra, kept beside the bundle; otherwise Message says why. */
DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnTerrainTilesFetchedBP, bool, bSuccess, const FString&, TilesZipPath, const FString&, Message);

/**
 * Fetches the terrain tiles Extra of an MPB 3.0 order, so "Stream into Cesium" works on a 3.0 bundle as it
 * does on a 2.x one. A 3.x bundle holds no terrain tiles: the platform lists them as an Extra beside the
 * bundle, and a signed-in plugin downloads them (spec/format.md 9.9, 12).
 *
 * Fetch(BundleZipPath) is the whole flow, asked for when the curator streams: it lists the vault, takes the
 * Extra the platform lists for the bundle's order, mints its link, downloads it and keeps it as
 * `TerrainTiles.zip` beside the bundle's zip. It runs once per order: tiles already beside the bundle are
 * reused and nothing is requested. It does the work only for a bundle in the vault cache (a zip the plugin
 * downloaded, which has a folder of its own to keep the file in); a zip from anywhere else is refused with the
 * reason, and so is an order the platform lists no terrain tiles for. Pure decisions are
 * FMantlePlaceTerrainTilesLogic's; this class is the impure shell around them.
 *
 * Asynchronous: OnFetched fires once, possibly before Fetch returns (a reuse or a refusal). Bind it first.
 * Nothing here streams; on success the caller streams again (UMantlePlaceImporterLibrary::StreamBundleIntoCesium).
 */
UCLASS(BlueprintType)
class MANTLEPLACEEDITOR_API UMantlePlaceTerrainTilesFetcher : public UObject
{
	GENERATED_BODY()

public:
	/** Fired once per Fetch, when it settles. */
	UPROPERTY(BlueprintAssignable, Category = "Mantle Place|Cesium")
	FMantlePlaceOnTerrainTilesFetchedBP OnFetched;

	/**
	 * Make the terrain tiles of the 3.0 bundle at BundleZipPath available beside it. Returns false, and fires
	 * nothing, only when a fetch is already running; every other outcome is reported through OnFetched.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Cesium")
	bool Fetch(const FString& BundleZipPath);

	/** True while a fetch is running. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Cesium")
	bool IsBusy() const { return Step != EStep::Idle; }

	//~ Begin UObject interface
	virtual void BeginDestroy() override;
	//~ End UObject interface

private:
	enum class EStep : uint8
	{
		Idle,
		Listing,     // the vault list is in flight
		Minting,     // the Extra's download link is being minted
		Downloading, // the link is being fetched
	};

	/** Construct and wire the vault client on the editor's one auth session (idempotent). */
	void EnsureClient();

	void HandleListedNative(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void HandlePresignedNative(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message);
	void HandleDownloaded(const TArray<uint8>& Bytes, int32 ResponseCode, bool bConnected);

	/** Settle: forget the run and fire OnFetched. */
	void Finish(bool bSuccess, const FString& Message);

	UPROPERTY()
	TObjectPtr<UMantlePlaceVaultClient> VaultClient;

	EStep Step = EStep::Idle;
	FString ActiveBundleZip;
	FString ActiveOrderId;
	FString ActiveTilesPath;
	FMantlePlaceVaultExtra ActiveExtra;
};
