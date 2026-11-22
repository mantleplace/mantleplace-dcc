// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceEditorSettings.h"

struct FMantlePlaceRasterValueMapping
{
	double MinValue = 0.0;
	double MaxValue = 0.0;
	FString ToValueFormula;
	FString Units;
	bool bHasNodata = false;
	double NodataValue = 0.0;
	double TrueValue = 0.0;
	double FalseValue = 0.0;
	TArray<int32> Classes;
	TArray<FString> Bands;
	bool bIdentity = false;
};

struct FMantlePlaceUeReadyRaster
{
	FString Path;
	FString Sha256;
	FString Encoding;
	int32 Width = 0;
	int32 Height = 0;
	int64 SizeBytes = 0;
	FMantlePlaceRasterValueMapping ValueMapping;
};

struct FMantlePlaceLandscapeLayer
{
	FString Name;
	FString Path;
	FString Sha256;
	TArray<FString> Materials;
	TArray<FMantlePlaceUeReadyRaster> UeReady;
};

struct FMantlePlaceGeoreferenceBlock
{
	int32 Epsg = 0;
	FString CrsProjected;
	bool bHasOrigin = false;
	double OriginEastingM = 0.0;
	double OriginNorthingM = 0.0;
	double GroundOrthometricHM = 0.0;
	bool bHasGeographicOrigin = false;
	double OriginLongitudeDeg = 0.0;
	double OriginLatitudeDeg = 0.0;
	FString PlanetShape;
};

struct FMantlePlaceCesiumBlock
{
	bool bHasTerrain = false;
	FString TerrainPath;
	int32 TerrainTileCount = 0;
	bool bHasBbox = false;
	double BboxWestDeg = 0.0;
	double BboxSouthDeg = 0.0;
	double BboxEastDeg = 0.0;
	double BboxNorthDeg = 0.0;
};

struct FMantlePlaceHeightmapBlock
{
	bool bPresent = false;
	FString Path;
	int32 Resolution = 0;
	int32 SectionSizeQuads = 0;
	int32 SectionsPerComponent = 0;
	int32 ComponentCountX = 0;
	int32 ComponentCountY = 0;
	double ScaleXPercent = 0.0;
	double ScaleYPercent = 0.0;
	double ScaleZPercent = 0.0;
	double LocationZOffsetCm = 0.0;
	double ZMinM = 0.0;
	double ZMaxM = 0.0;
	bool bRow0IsNorth = true;
	FString Sha256;
	double GridCentreEastM = 0.0;
	double GridCentreNorthM = 0.0;

	int32 GetQuadsPerComponent() const { return SectionsPerComponent * SectionSizeQuads; }
};

struct FMantlePlaceDrapeBlock
{
	bool bPresent = false;
	FString Path;
	double LeftM = 0.0;
	double BottomM = 0.0;
	double RightM = 0.0;
	double TopM = 0.0;
	FString Sha256;
	FString Alignment;
};

struct FMantlePlaceMeshBlock
{
	bool bPresent = false;
	FString Path;
	FString UpAxis;
	bool bNaniteRecommended = false;
	FString Sha256;
	FString AbsentReason;
};

enum class EMantlePlaceTreeLevelForm : uint8
{
	Absent,
	Pointer,
	RowCut,
	SameAs,
};

struct FMantlePlaceTreeLevelEntry
{
	EMantlePlaceTreeLevelForm Form = EMantlePlaceTreeLevelForm::Absent;
	FString Path;
	FString Sha256;
	int32 Count = 0;
	FString Crs;
	FString Units;
	FString HorizontalUnits;
	int32 Rows = 0;
	FString SameAs;
};

struct FMantlePlaceFoliagePointsBlock
{
	bool bPresent = false;
	FString Path;
	FString Sha256;
	int32 Count = 0;
	FString Crs;
	FString Units;
	FMantlePlaceTreeLevelEntry LevelMax;
	FMantlePlaceTreeLevelEntry LevelMed;
	FMantlePlaceTreeLevelEntry LevelMin;
	FString HorizontalUnits;
	FString FoliageTypeVocabulary;
	bool bSiteFrame = false;
	double SiteHeightM = 0.0;
};

struct FMantlePlaceLandscapeLayersBlock
{
	bool bPresent = false;
	TArray<FMantlePlaceLandscapeLayer> Layers;
};

struct FMantlePlaceBuildingsBlock
{
	bool bPresent = false;
	FString Path;
	FString UpAxis;
	FString Sha256;
};

struct FMantlePlaceRoadSplinesBlock
{
	bool bPresent = false;
	FString Path;
	FString Sha256;

	bool bGpkgPresent = false;
	FString GpkgPath;
	FString GpkgSha256;
	TArray<FString> GpkgTables;
	FString VerticalDatum;

	bool bEnginePresent = false;
	FString EnginePath;
	FString EngineSha256;
	TArray<FString> EngineTables;
	FString EngineVerticalDatum;

	bool bSiteCsvPresent = false;
	FString SiteCsvPath;
	FString SiteCsvSha256;
	double SiteHeightM = 0.0;
};

struct FMantlePlaceRecordFrame
{
	bool bStated = false;
	FString Type;
	FString Crs;
	FString HorizontalUnit;
	FString VerticalUnit;
};

struct FMantlePlaceRecordFile
{
	FString Kind;
	FString Path;
	FString Sha256;
	FString Source;
	FString License;
	FMantlePlaceRecordFrame Frame;
	bool IsDxf() const { return Path.EndsWith(TEXT(".dxf"), ESearchCase::IgnoreCase); }
};

struct FMantlePlaceRecordPointer
{
	FString RecordHolder;
	FString Jurisdiction;
	FString State;
	FString Link;
	FString Reason;
	FString Basis;
	FString SourceId;
};

struct FMantlePlaceRecordLayer
{
	FString Family;
	TArray<FMantlePlaceRecordFile> Files;
	TArray<FMantlePlaceRecordPointer> Pointers;
	FString Credit;
	FString Disclaimer;
	FString Notice;
};

struct FMantlePlaceSkippedSource
{
	FString Layer;
	FString SourceId;
	FString RecordHolder;
	FString Reason;
};

struct FMantlePlaceRecordNotHeld
{
	FString Family;
	FString Path;
	FString Reason;
};

struct FMantlePlacePublicRecordsBlock
{
	bool bPresent = false;
	FString BundleNotice;
	TArray<FMantlePlaceRecordNotHeld> NotHeld;
	TArray<FMantlePlaceRecordLayer> Layers;
	TArray<FMantlePlaceSkippedSource> SkippedSources;
};
