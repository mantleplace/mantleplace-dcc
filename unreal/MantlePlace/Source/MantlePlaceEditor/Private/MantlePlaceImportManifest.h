// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceManifestBlocks.h"
#include "MantlePlaceOdblLogic.h"

class FJsonObject;
struct FMantlePlaceRoadSplinesCandidate;
struct FMantlePlaceRoadSplinesDatum;
struct FMantlePlaceTreePointsFrame;

struct FMantlePlaceDeliveryFacts
{
	bool bDeclared = false;
	FString UnitSystem;
	FString LinearUnit;
	bool bHasHorizontalEpsg = false;
	int32 HorizontalEpsg = 0;
	FString Label;
};

struct FMantlePlaceVaultManifest
{
	bool bValid = false;
	FString JobId;
	FString OrderId;
	FString Version;
	FString DeliveryModel;
	FMantlePlaceDeliveryFacts Delivery;

	FString SiteVerticalDatum;

	double LevelOffsetEastM = 0.0;
	double LevelOffsetNorthM = 0.0;

	TMap<FString, FMantlePlaceFileLicensing> FileLicensing;
	TArray<FMantlePlaceAttributionSource> Attribution;

	FMantlePlaceArchiveView Archive;

	bool bFormatThree = false;
	TMap<FName, FString> FormatSkips;
	TArray<FString> FormatNotes;

	FMantlePlaceGeoreferenceBlock Georef;
	FMantlePlaceCesiumBlock Cesium;

	FMantlePlaceHeightmapBlock Heightmap;
	FMantlePlaceDrapeBlock Drape;
	FMantlePlaceMeshBlock Mesh;
	FMantlePlaceFoliagePointsBlock Foliage;
	FMantlePlaceLandscapeLayersBlock LandscapeLayers;
	FMantlePlaceBuildingsBlock Buildings;
	FMantlePlaceRoadSplinesBlock RoadSplines;
	FMantlePlacePublicRecordsBlock PublicRecords;

	static FVector ProjectedToUeCm(double DeltaEastingM, double DeltaNorthingM, double UpM);

	void SetLevelOffsetM(double EastM, double NorthM);
	bool HasLevelOffset() const;

	FVector ToLevelFrame(const FVector& FrameLocalUeCm) const;

	FVector2D GetAoiSizeUeCm() const;

	static FRotator GetMeshRotation();

	FVector GetLandscapeScale() const;

	FVector GetLandscapeSpawnLocation() const;

	FVector GetMeshLocation() const;

	void GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const;

	void GetDrapeUvTransform(FVector2D& OutScale, FVector2D& OutOffset) const;

	int32 GetQuadsPerComponent() const { return Heightmap.GetQuadsPerComponent(); }

	const FMantlePlaceLandscapeLayer* FindLandscapeLayer(const TCHAR* Name) const;

	FMantlePlaceTreePointsFrame GetFoliagePointsFrame() const;

	FMantlePlaceRoadSplinesDatum GetRoadSplinesDatum() const;

	TArray<FMantlePlaceRoadSplinesCandidate> GetRoadSplinesPlan() const;
};

namespace MantlePlaceImportManifest
{
FMantlePlaceVaultManifest Parse(const FString& JsonText, FString& OutError);

int32 ParseEpsg(const FString& CrsString);

bool IsBundleMaterialized(const TSharedPtr<FJsonObject>& Root);

FString DeriveCesiumTerrainPrefix(const FString& CesiumTerrainPath);
}
