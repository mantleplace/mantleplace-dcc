// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTerrainTilesLogic.h"

#include "Misc/Paths.h"
#include "MantlePlaceVaultTypes.h"

FString FMantlePlaceTerrainTilesLogic::TilesZipBeside(const FString& BundleZipPath)
{
	return FPaths::Combine(FPaths::GetPath(BundleZipPath), TilesFileName());
}

bool FMantlePlaceTerrainTilesLogic::IsInVaultCache(const FString& BundleZipPath, const FString& CacheRoot)
{
	if (BundleZipPath.IsEmpty() || CacheRoot.IsEmpty())
	{
		return false;
	}
	FString Zip = FPaths::ConvertRelativePathToFull(BundleZipPath);
	FString Root = FPaths::ConvertRelativePathToFull(CacheRoot);
	FPaths::NormalizeFilename(Zip);
	FPaths::NormalizeFilename(Root);
	FPaths::CollapseRelativeDirectories(Zip);
	FPaths::CollapseRelativeDirectories(Root);
	if (!Root.EndsWith(TEXT("/")))
	{
		Root += TEXT("/");
	}
	return Zip.StartsWith(Root, ESearchCase::IgnoreCase);
}

FMantlePlaceTerrainTilesLogic::FTileset FMantlePlaceTerrainTilesLogic::FindTileset(const TArray<FString>& EntryNames)
{
	FTileset Found;
	int32 FoundDepth = MAX_int32;
	for (const FString& Name : EntryNames)
	{
		if (Name.IsEmpty() || Name.EndsWith(TEXT("/"))
			|| !FPaths::GetCleanFilename(Name).Equals(TEXT("layer.json"), ESearchCase::IgnoreCase))
		{
			continue;
		}
		const FString Dir = FPaths::GetPath(Name);
		if (Dir.IsEmpty())
		{
			continue;
		}
		int32 Depth = 0;
		for (const TCHAR Ch : Dir)
		{
			Depth += (Ch == TEXT('/')) ? 1 : 0;
		}
		if (Depth < FoundDepth || (Depth == FoundDepth && Name < Found.LayerJsonPath))
		{
			FoundDepth = Depth;
			Found.bFound = true;
			Found.LayerJsonPath = Name;
			Found.Prefix = Dir + TEXT("/");
		}
	}
	if (Found.bFound)
	{
		for (const FString& Name : EntryNames)
		{
			if (Name.StartsWith(Found.Prefix, ESearchCase::IgnoreCase) && Name.EndsWith(TEXT(".terrain"), ESearchCase::IgnoreCase))
			{
				++Found.TileCount;
			}
		}
	}
	return Found;
}

FMantlePlaceTerrainTilesLogic::ESource FMantlePlaceTerrainTilesLogic::DecideSource(bool bFormatThree, bool bTilesBesideUsable)
{
	if (!bFormatThree)
	{
		return ESource::Bundled;
	}
	return bTilesBesideUsable ? ESource::ExtraBeside : ESource::ExtraMissing;
}

FMantlePlaceTerrainTilesLogic::EFetchPlan FMantlePlaceTerrainTilesLogic::PlanFetch(
	bool bTilesBesideUsable, const FMantlePlaceVaultItem* Item)
{
	if (bTilesBesideUsable)
	{
		return EFetchPlan::Reuse;
	}
	const FMantlePlaceVaultExtra* Tiles = Item != nullptr ? Item->FindExtra(MantlePlaceTerrainTilesExtraId()) : nullptr;
	if (Tiles == nullptr)
	{
		return EFetchPlan::NoTilesListed;
	}
	return Tiles->bLocked ? EFetchPlan::NotIncluded : EFetchPlan::Fetch;
}

bool FMantlePlaceTerrainTilesLogic::SizeMatches(int64 StatedBytes, int64 ActualBytes)
{
	return StatedBytes <= 0 || StatedBytes == ActualBytes;
}

FString FMantlePlaceTerrainTilesLogic::NoTilesListedReason()
{
	return TEXT("Streaming into Cesium is not available for this order: the platform lists no terrain tiles for it.");
}

FString FMantlePlaceTerrainTilesLogic::NotIncludedReason()
{
	return MantlePlaceNotIncludedWords();
}

FString FMantlePlaceTerrainTilesLogic::NoTilesBesideReason()
{
	return FString::Printf(TEXT("Streaming into Cesium is not available for this bundle: an MPB 3.0 bundle holds no terrain "
		"tiles, and this zip has no %s beside it. Import the order from the vault to have the plugin fetch them."), TilesFileName());
}

FString FMantlePlaceTerrainTilesLogic::NotFetchedYetMessage()
{
	return TEXT("This MPB 3.0 order's terrain tiles are not on disk yet. Fetch them (UMantlePlaceTerrainTilesFetcher), "
		"then stream again.");
}

FString FMantlePlaceTerrainTilesLogic::NotATilesetReason()
{
	return FString::Printf(TEXT("The terrain tiles download holds no tileset: no layer.json inside a folder. %s was not kept."),
		TilesFileName());
}

FString FMantlePlaceTerrainTilesLogic::NotThreeReason()
{
	return TEXT("This is not an MPB 3.0 bundle: it holds its own terrain tiles, and nothing is fetched for it.");
}
