// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleReaderLogic.h"

struct FMantlePlaceVaultManifest;

struct FMantlePlaceLandscapeLayout
{
	int32 SectionSizeQuads = 0;
	int32 SectionsPerComponent = 0;
	int32 ComponentCountX = 0;
	int32 ComponentCountY = 0;
};

struct FMantlePlaceManifestThreeLogic
{
	static const TCHAR* HeightmapPath() { return TEXT("Elevation/Elevation.png"); }
	static const TCHAR* DrapePath() { return TEXT("Imagery/Imagery.png"); }
	static const TCHAR* TerrainMeshPath() { return TEXT("Elevation/Terrain.glb"); }
	static const TCHAR* BuildingsPath() { return TEXT("Basemap/Buildings.glb"); }
	static const TCHAR* TreesPath() { return TEXT("Landcover/Trees.csv"); }
	static const TCHAR* RoadsPath() { return TEXT("Basemap/Roads.csv"); }
	static const TCHAR* MaterialsFolder() { return TEXT("Materials/"); }
	static const TCHAR* RecordsFolder() { return TEXT("Records/"); }

	static const TArray<FString>& RequestedFiles();

	static TArray<FString> ListedAmong(const FMantlePlaceBundleRead& Read, const TArray<FString>& Names);

	static bool ToVaultManifest(const FMantlePlaceBundleRead& Read, const FString& ManifestJson,
		FMantlePlaceVaultManifest& OutManifest, FString& OutError);

	static bool ChooseLandscapeLayout(int32 Width, int32 Height, FMantlePlaceLandscapeLayout& OutLayout);

	static FString NoHostReadyFileReason(const FString& What);
	static FString NotHeldReason(const FString& Path, const FString& StatedReason);
};
