// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportManifest.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceRoadSplinesLogic.h"
#include "MantlePlaceTreePointsLogic.h"
#include "MantlePlaceVaultTypes.h"

#include "Dom/JsonObject.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

FVector FMantlePlaceVaultManifest::ProjectedToUeCm(double DeltaEastingM, double DeltaNorthingM, double UpM)
{
	return FVector(DeltaNorthingM * 100.0, DeltaEastingM * 100.0, UpM * 100.0);
}

FVector2D FMantlePlaceVaultManifest::GetAoiSizeUeCm() const
{
	return FVector2D(
		Heightmap.ComponentCountY * GetQuadsPerComponent() * Heightmap.ScaleYPercent,
		Heightmap.ComponentCountX * GetQuadsPerComponent() * Heightmap.ScaleXPercent);
}

FRotator FMantlePlaceVaultManifest::GetMeshRotation()
{
	return FRotator(0.0, 90.0, 0.0);
}

FVector FMantlePlaceVaultManifest::GetLandscapeScale() const
{
	return FVector(Heightmap.ScaleYPercent, Heightmap.ScaleXPercent, Heightmap.ScaleZPercent);
}

void FMantlePlaceVaultManifest::SetLevelOffsetM(double EastM, double NorthM)
{
	LevelOffsetEastM = EastM;
	LevelOffsetNorthM = NorthM;
}

bool FMantlePlaceVaultManifest::HasLevelOffset() const
{
	return LevelOffsetEastM != 0.0 || LevelOffsetNorthM != 0.0;
}

FVector FMantlePlaceVaultManifest::ToLevelFrame(const FVector& FrameLocalUeCm) const
{
	const FVector Offset = ProjectedToUeCm(LevelOffsetEastM, LevelOffsetNorthM, 0.0);
	return FrameLocalUeCm + Offset;
}

FVector FMantlePlaceVaultManifest::GetLandscapeSpawnLocation() const
{
	const FVector2D AoiSize = GetAoiSizeUeCm();
	const FVector Centre = ProjectedToUeCm(Heightmap.GridCentreEastM, Heightmap.GridCentreNorthM, 0.0);
	return ToLevelFrame(FVector(Centre.X - AoiSize.X / 2.0, Centre.Y - AoiSize.Y / 2.0, Heightmap.LocationZOffsetCm));
}

FVector FMantlePlaceVaultManifest::GetMeshLocation() const
{
	return ToLevelFrame(FVector(0.0, 0.0, Georef.GroundOrthometricHM * 100.0));
}

void FMantlePlaceVaultManifest::GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const
{
	const FVector Min = ProjectedToUeCm(Drape.LeftM - Georef.OriginEastingM, Drape.BottomM - Georef.OriginNorthingM, 0.0);
	const FVector Size = ProjectedToUeCm(Drape.RightM - Drape.LeftM, Drape.TopM - Drape.BottomM, 0.0);
	OutMin = FVector2D(Min.X, Min.Y);
	OutSize = FVector2D(Size.X, Size.Y);
}

void FMantlePlaceVaultManifest::GetDrapeUvTransform(FVector2D& OutScale, FVector2D& OutOffset) const
{
	OutScale = FVector2D(1.0, 1.0);
	OutOffset = FVector2D(0.0, 0.0);

	FVector2D DrapeMin, DrapeSize;
	GetDrapeWorldRect(DrapeMin, DrapeSize);
	if (DrapeSize.X <= 0.0 || DrapeSize.Y <= 0.0)
	{
		return;
	}

	const FVector2D AoiSize = GetAoiSizeUeCm();
	const FVector Centre = ProjectedToUeCm(Heightmap.GridCentreEastM, Heightmap.GridCentreNorthM, 0.0);
	const FVector2D AoiMin(Centre.X - AoiSize.X / 2.0, Centre.Y - AoiSize.Y / 2.0);

	OutScale = FVector2D(AoiSize.X / DrapeSize.X, AoiSize.Y / DrapeSize.Y);
	OutOffset = FVector2D(
		(AoiMin.X - DrapeMin.X) / DrapeSize.X,
		(AoiMin.Y - DrapeMin.Y) / DrapeSize.Y);
}

const FMantlePlaceLandscapeLayer* FMantlePlaceVaultManifest::FindLandscapeLayer(const TCHAR* Name) const
{
	return LandscapeLayers.Layers.FindByPredicate(
		[Name](const FMantlePlaceLandscapeLayer& Layer) { return Layer.Name == Name; });
}

FMantlePlaceTreePointsFrame FMantlePlaceVaultManifest::GetFoliagePointsFrame() const
{
	static const FString FirstVersionStatingTreePointsFrame = TEXT("1.3.0");

	FMantlePlaceTreePointsFrame Frame;
	Frame.Crs = Foliage.Crs;
	Frame.Units = Foliage.Units;
	Frame.HorizontalUnits = Foliage.HorizontalUnits;
	Frame.HostCrs = Georef.CrsProjected;
	Frame.bRequired = !MantlePlaceIsManifestVersionBelowFloor(Version, FirstVersionStatingTreePointsFrame);
	return Frame;
}

FMantlePlaceRoadSplinesDatum FMantlePlaceVaultManifest::GetRoadSplinesDatum() const
{
	static const FString FirstVersionStatingHeightDatum = TEXT("1.8.0");

	FMantlePlaceRoadSplinesDatum Datum;
	Datum.Stated = RoadSplines.VerticalDatum;
	Datum.bRequired = !MantlePlaceIsManifestVersionBelowFloor(Version, FirstVersionStatingHeightDatum);
	return Datum;
}

TArray<FMantlePlaceRoadSplinesCandidate> FMantlePlaceVaultManifest::GetRoadSplinesPlan() const
{
	return FMantlePlaceRoadSplinesLogic::PlanSources(
	    RoadSplines.bEnginePresent, RoadSplines.EngineVerticalDatum,
	    RoadSplines.bPresent || RoadSplines.bGpkgPresent, GetRoadSplinesDatum());
}

using MantlePlaceManifestJson::GetInt;
using MantlePlaceManifestJson::GetNumber;
using MantlePlaceManifestJson::GetObject;
using MantlePlaceManifestJson::GetString;

int32 MantlePlaceImportManifest::ParseEpsg(const FString& CrsString)
{
	FString Digits;
	for (const TCHAR Ch : CrsString)
	{
		if (FChar::IsDigit(Ch))
		{
			Digits.AppendChar(Ch);
		}
	}
	return Digits.IsEmpty() ? 0 : FCString::Atoi(*Digits);
}

FString MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(const FString& CesiumTerrainPath)
{
	const FString Dir = FPaths::GetPath(CesiumTerrainPath);
	return Dir.IsEmpty() ? FString() : (Dir + TEXT("/"));
}

bool MantlePlaceImportManifest::IsBundleMaterialized(const TSharedPtr<FJsonObject>& Root)
{
	if (!Root.IsValid())
	{
		return false;
	}

	if (const TSharedPtr<FJsonObject>* HostsPtr = GetObject(Root, TEXT("hosts")))
	{
		if ((*HostsPtr)->Values.Num() > 0)
		{
			return true;
		}
	}

	if (const TSharedPtr<FJsonObject>* VectorPtr = GetObject(Root, TEXT("vector")))
	{
		const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
		if ((*VectorPtr)->TryGetArrayField(TEXT("layers"), Layers) && Layers != nullptr && Layers->Num() > 0)
		{
			return true;
		}
	}

	return false;
}

namespace
{
bool ParseGeoreference(const TSharedPtr<FJsonObject>& Unreal, FMantlePlaceGeoreferenceBlock& Georef, FString& OutError)
{
	const TSharedPtr<FJsonObject>* GeoPtr = GetObject(Unreal, TEXT("georeference"));
	if (GeoPtr == nullptr)
	{
		return true;
	}
	const TSharedPtr<FJsonObject> Geo = *GeoPtr;
	Georef.CrsProjected = GetString(Geo, TEXT("crs_projected"));
	Georef.Epsg = MantlePlaceImportManifest::ParseEpsg(Georef.CrsProjected);
	if (const TSharedPtr<FJsonObject>* OriginPtr = GetObject(Geo, TEXT("origin")))
	{
		const TSharedPtr<FJsonObject> Origin = *OriginPtr;
		Georef.GroundOrthometricHM = GetNumber(Origin, TEXT("ground_orthometric_h_m"));
		if (const TSharedPtr<FJsonObject>* UtmPtr = GetObject(Origin, TEXT("utm")))
		{
			Georef.OriginEastingM = GetNumber(*UtmPtr, TEXT("easting_m"));
			Georef.OriginNorthingM = GetNumber(*UtmPtr, TEXT("northing_m"));
			Georef.bHasOrigin = (*UtmPtr)->HasTypedField<EJson::Number>(TEXT("easting_m"))
				&& (*UtmPtr)->HasTypedField<EJson::Number>(TEXT("northing_m"));
			if (Georef.Epsg == 0)
			{
				Georef.Epsg = GetInt(*UtmPtr, TEXT("epsg"));
			}
		}
	}

	Georef.PlanetShape = GetString(Geo, TEXT("planet_shape"));
	if (!Georef.PlanetShape.IsEmpty() && !Georef.PlanetShape.Equals(TEXT("Flat")))
	{
		OutError = FString::Printf(
			TEXT("Unsupported planet_shape \"%s\": the v1 plugin supports only \"Flat\"."),
			*Georef.PlanetShape);
		return false;
	}
	return true;
}

void ParseCesium(const TSharedPtr<FJsonObject>& Root, FMantlePlaceCesiumBlock& Cesium)
{
	if (const TSharedPtr<FJsonObject>* BboxPtr = GetObject(Root, TEXT("bbox")))
	{
		const TSharedPtr<FJsonObject> Bbox = *BboxPtr;
		Cesium.BboxWestDeg = GetNumber(Bbox, TEXT("west"));
		Cesium.BboxSouthDeg = GetNumber(Bbox, TEXT("south"));
		Cesium.BboxEastDeg = GetNumber(Bbox, TEXT("east"));
		Cesium.BboxNorthDeg = GetNumber(Bbox, TEXT("north"));
		Cesium.bHasBbox = (Cesium.BboxEastDeg > Cesium.BboxWestDeg) && (Cesium.BboxNorthDeg > Cesium.BboxSouthDeg);
	}
	if (const TSharedPtr<FJsonObject>* LayoutPtr = GetObject(Root, TEXT("layout")))
	{
		Cesium.TerrainPath = GetString(*LayoutPtr, TEXT("cesium_terrain"));
	}
	if (const TSharedPtr<FJsonObject>* CesiumTerrainPtr = GetObject(Root, TEXT("cesium_terrain")))
	{
		Cesium.TerrainTileCount = GetInt(*CesiumTerrainPtr, TEXT("tile_count"));
	}
	Cesium.bHasTerrain = !Cesium.TerrainPath.IsEmpty();
}

void ParseDelivery(const TSharedPtr<FJsonObject>& Root, FMantlePlaceDeliveryFacts& Delivery)
{
	const TSharedPtr<FJsonObject>* DeliveryPtr = GetObject(Root, TEXT("delivery"));
	if (DeliveryPtr == nullptr)
	{
		return;
	}
	Delivery.bDeclared = true;
	Delivery.UnitSystem = GetString(*DeliveryPtr, TEXT("unit_system"));
	Delivery.LinearUnit = GetString(*DeliveryPtr, TEXT("linear_unit"));
	Delivery.Label = GetString(*DeliveryPtr, TEXT("label"));
	double HorizontalEpsg = 0.0;
	Delivery.bHasHorizontalEpsg = (*DeliveryPtr)->TryGetNumberField(TEXT("horizontal_epsg"), HorizontalEpsg);
	Delivery.HorizontalEpsg = Delivery.bHasHorizontalEpsg ? FMath::RoundToInt(HorizontalEpsg) : 0;
}
}

namespace
{
void ParseFileLicensing(const TSharedPtr<FJsonObject>& Root, TMap<FString, FMantlePlaceFileLicensing>& Out)
{
	const TSharedPtr<FJsonObject>* EntriesPtr = GetObject(Root, TEXT("file_entries"));
	if (EntriesPtr == nullptr)
	{
		return;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*EntriesPtr)->Values)
	{
		const TSharedPtr<FJsonObject>* EntryPtr = nullptr;
		if (!Pair.Value.IsValid() || !Pair.Value->TryGetObject(EntryPtr) || EntryPtr == nullptr)
		{
			continue;
		}
		FMantlePlaceFileLicensing Entry;
		const bool bRoleStated = (*EntryPtr)->HasTypedField<EJson::String>(TEXT("odbl_role"));
		Entry.Role = MantlePlaceOdblLogic::ParseRole(bRoleStated, GetString(*EntryPtr, TEXT("odbl_role")));
		Entry.License = GetString(*EntryPtr, TEXT("license"));
		const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
		if ((*EntryPtr)->TryGetArrayField(TEXT("sources"), Sources) && Sources != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& SourceValue : *Sources)
			{
				const TSharedPtr<FJsonObject>* SourcePtr = nullptr;
				if (SourceValue.IsValid() && SourceValue->TryGetObject(SourcePtr) && SourcePtr != nullptr)
				{
					Entry.Sources.Add({ GetString(*SourcePtr, TEXT("name")), GetString(*SourcePtr, TEXT("license")) });
				}
			}
		}
		Out.Add(Pair.Key, MoveTemp(Entry));
	}
}

void ParseArchiveView(const TSharedPtr<FJsonObject>& Root, FMantlePlaceArchiveView& Out)
{
	const TSharedPtr<FJsonObject>* ViewPtr = GetObject(Root, TEXT("view"));
	const TArray<TSharedPtr<FJsonValue>>* Parts = nullptr;
	if (ViewPtr == nullptr || !(*ViewPtr)->TryGetArrayField(TEXT("parts"), Parts) || Parts == nullptr)
	{
		return;
	}
	Out.Kind = GetString(*ViewPtr, TEXT("kind"));
	for (const TSharedPtr<FJsonValue>& Value : *Parts)
	{
		FString Part;
		if (Value.IsValid() && Value->TryGetString(Part))
		{
			Out.Parts.Add(MoveTemp(Part));
		}
	}
	if (const TSharedPtr<FJsonObject>* FilesPtr = GetObject(Root, TEXT("files")))
	{
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*FilesPtr)->Values)
		{
			const TSharedPtr<FJsonObject>* EntryPtr = nullptr;
			if (Pair.Value.IsValid() && Pair.Value->TryGetObject(EntryPtr) && EntryPtr != nullptr)
			{
				const FString Part = GetString(*EntryPtr, TEXT("part"));
				if (!Part.IsEmpty())
				{
					Out.FileParts.Add(Pair.Key, Part);
				}
			}
		}
	}
	Out.bDeclared = true;
}

void ParseAttribution(const TSharedPtr<FJsonObject>& Root, TArray<FMantlePlaceAttributionSource>& Out)
{
	const TSharedPtr<FJsonObject>* AttributionPtr = GetObject(Root, TEXT("attribution"));
	const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
	if (AttributionPtr == nullptr || !(*AttributionPtr)->TryGetArrayField(TEXT("sources"), Sources) || Sources == nullptr)
	{
		return;
	}
	for (const TSharedPtr<FJsonValue>& SourceValue : *Sources)
	{
		const TSharedPtr<FJsonObject>* SourcePtr = nullptr;
		if (SourceValue.IsValid() && SourceValue->TryGetObject(SourcePtr) && SourcePtr != nullptr)
		{
			FMantlePlaceAttributionSource Source;
			Source.ProviderId = GetString(*SourcePtr, TEXT("provider_id"));
			Source.AttributionText = GetString(*SourcePtr, TEXT("attribution_text"));
			Source.License = GetString(*SourcePtr, TEXT("license"));
			Source.LicenseUrl = GetString(*SourcePtr, TEXT("license_url"));
			Out.Add(MoveTemp(Source));
		}
	}
}

}

FMantlePlaceVaultManifest MantlePlaceImportManifest::Parse(const FString& JsonText, FString& OutError)
{
	FMantlePlaceVaultManifest M;

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonText);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("manifest.json is not valid JSON.");
		return M;
	}

	M.JobId = GetString(Root, TEXT("job_id"));
	M.OrderId = GetString(Root, TEXT("order_id"));
	M.Version = GetString(Root, TEXT("version"));

	const FMantlePlaceManifestVersion Parsed = FMantlePlaceManifestVersion::Parse(M.Version);
	const FMantlePlaceManifestVersion Floor =
	    FMantlePlaceManifestVersion::Parse(MantlePlaceMinSupportedManifestVersion);
	if (!Parsed.bValid || Parsed < Floor)
	{
		OutError = FMantlePlaceBundleReaderLogic::BelowFloorMessage(M.Version, MantlePlaceMinSupportedManifestVersion);
		return M;
	}

	if (Parsed.Major > MantlePlaceMaxSupportedManifestMajor)
	{
		OutError = FMantlePlaceBundleReaderLogic::NewerMajorMessage(M.Version, MantlePlaceMaxSupportedManifestMajor);
		return M;
	}

	ParseCesium(Root, M.Cesium);
	if (const TSharedPtr<FJsonObject>* PackagingPtr = GetObject(Root, TEXT("packaging")))
	{
		M.DeliveryModel = GetString(*PackagingPtr, TEXT("delivery_model"));
	}
	ParseDelivery(Root, M.Delivery);
	ParseFileLicensing(Root, M.FileLicensing);
	ParseAttribution(Root, M.Attribution);
	ParseArchiveView(Root, M.Archive);
	if (const TSharedPtr<FJsonObject>* LocationPtr = GetObject(Root, TEXT("location")))
	{
		if (const TSharedPtr<FJsonObject>* SiteOriginPtr = GetObject(*LocationPtr, TEXT("site_origin")))
		{
			M.SiteVerticalDatum = GetString(*SiteOriginPtr, TEXT("vertical_datum"));
		}
	}

	TSharedPtr<FJsonObject> Unreal;
	if (const TSharedPtr<FJsonObject>* HostsRootPtr = GetObject(Root, TEXT("hosts")))
	{
		if (const TSharedPtr<FJsonObject>* UnrealPtr = GetObject(*HostsRootPtr, TEXT("unreal")))
		{
			Unreal = *UnrealPtr;
		}
	}

	const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
	const FMantlePlaceParseInput Input{ Root, Unreal };
	if (!ParseGeoreference(Unreal, M.Georef, OutError))
	{
		return M;
	}
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Handler->ParseFields(Input, M, OutError))
		{
			return M;
		}
	}

	if (!Unreal.IsValid())
	{
		OutError = IsBundleMaterialized(Root)
			? TEXT("This bundle hasn't generated its Unreal formats yet (it carries formats for other "
			       "tools, but nothing Unreal imports). Open your vault at mantle.place/vault, choose "
			       "\"Unreal Engine\" (or Generate all), then re-download. You can still preview it now "
			       "with \"Stream into Cesium\" (this bundle ships Cesium terrain).")
			: TEXT("This bundle hasn't generated its Unreal formats yet. Open your vault at "
			       "mantle.place/vault, choose \"Unreal Engine\" (or Generate all), then re-download. "
			       "You can still preview it now with \"Stream into Cesium\" (this bundle ships Cesium terrain).");
		return M;
	}

	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Handler->CrossCheck(M, OutError))
		{
			return M;
		}
	}

	M.bValid = M.Heightmap.bPresent || M.Mesh.bPresent;
	if (!M.bValid)
	{
		if (M.DeliveryModel == TEXT("base_on_demand"))
		{
			OutError = TEXT("This bundle hasn't generated its Unreal formats yet. Open your vault at "
			                "mantle.place/vault, choose \"Unreal Engine\" (or Generate all), then re-download. "
			                "You can still preview it now with \"Stream into Cesium\" (this bundle ships Cesium terrain).");
		}
		else
		{
			OutError = TEXT("manifest \"unreal\" block has neither a usable heightmap nor a mesh.");
		}
	}
	return M;
}
