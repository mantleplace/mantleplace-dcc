// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;

struct FMantlePlaceTerrainTilesLogic
{
	static const TCHAR* TilesFileName() { return TEXT("TerrainTiles.zip"); }

	static FString TilesZipBeside(const FString& BundleZipPath);

	static bool IsInVaultCache(const FString& BundleZipPath, const FString& CacheRoot);

	struct FTileset
	{
		bool bFound = false;

		FString LayerJsonPath;

		FString Prefix;

		int32 TileCount = 0;
	};

	static FTileset FindTileset(const TArray<FString>& EntryNames);

	enum class ESource : uint8
	{
		Bundled,

		ExtraBeside,

		ExtraMissing,
	};

	static ESource DecideSource(bool bFormatThree, bool bTilesBesideUsable);

	enum class EFetchPlan : uint8
	{
		Reuse,

		Fetch,

		NoTilesListed,

		NotIncluded,
	};

	static EFetchPlan PlanFetch(bool bTilesBesideUsable, const FMantlePlaceVaultItem* Item);

	static bool SizeMatches(int64 StatedBytes, int64 ActualBytes);

	static FString NoTilesListedReason();
	static FString NotIncludedReason();
	static FString NoTilesBesideReason();
	static FString NotFetchedYetMessage();
	static FString NotATilesetReason();
	static FString NotThreeReason();
};
