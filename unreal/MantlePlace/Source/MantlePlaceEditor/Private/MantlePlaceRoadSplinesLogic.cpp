// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceRoadSplinesLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
// WGS84 ellipsoid + UTM constants (USGS Professional Paper 1395, Snyder eq. 8-9..8-15).
constexpr double SemiMajorM = 6378137.0;
constexpr double Flattening = 1.0 / 298.257223563;
constexpr double K0 = 0.9996;
constexpr double FalseEastingM = 500000.0;
constexpr double SouthFalseNorthingM = 10000000.0;

/** Convert one GeoJSON position array [lon, lat, z?] into the Local Projected Frame (UE cm). */
bool PositionToUeCm(
    const TArray<TSharedPtr<FJsonValue>>& Position,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    FVector& OutUeCm)
{
	if (Position.Num() < 2)
	{
		return false;
	}
	const double LonDeg = Position[0]->AsNumber();
	const double LatDeg = Position[1]->AsNumber();
	const double ZM = Position.Num() >= 3 ? Position[2]->AsNumber() : 0.0;

	double EastingM = 0.0, NorthingM = 0.0;
	if (!FMantlePlaceRoadSplinesLogic::LonLatToUtm(LonDeg, LatDeg, Epsg, EastingM, NorthingM))
	{
		return false;
	}
	// Same frame math as FMantlePlaceVaultManifest::GetDrapeWorldRect / GetMeshLocation:
	// East -> +X, North -> +Y, orthometric meters -> +Z, all relative to the AOI-centroid origin.
	OutUeCm = FVector(
	    (EastingM - OriginEastingM) * 100.0,
	    (NorthingM - OriginNorthingM) * 100.0,
	    ZM * 100.0);
	return true;
}

/** Append every part of a LineString/MultiLineString geometry as its own spline. */
void AppendGeometry(
    const TSharedPtr<FJsonObject>& Geometry,
    const FMantlePlaceRoadSpline& Prototype,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines)
{
	if (!Geometry.IsValid())
	{
		return;
	}
	FString Type;
	Geometry->TryGetStringField(TEXT("type"), Type);

	const TArray<TSharedPtr<FJsonValue>>* Coords = nullptr;
	if (!Geometry->TryGetArrayField(TEXT("coordinates"), Coords) || Coords == nullptr)
	{
		return;
	}

	// Normalize both shapes to an array of line strings.
	TArray<const TArray<TSharedPtr<FJsonValue>>*> Lines;
	if (Type == TEXT("LineString"))
	{
		Lines.Add(Coords);
	}
	else if (Type == TEXT("MultiLineString"))
	{
		for (const TSharedPtr<FJsonValue>& LineValue : *Coords)
		{
			const TArray<TSharedPtr<FJsonValue>>* Line = nullptr;
			if (LineValue.IsValid() && LineValue->TryGetArray(Line) && Line != nullptr)
			{
				Lines.Add(Line);
			}
		}
	}
	// Other geometry types (points, polygons) are not road centerlines - skip.

	for (const TArray<TSharedPtr<FJsonValue>>* Line : Lines)
	{
		FMantlePlaceRoadSpline Spline = Prototype;
		for (const TSharedPtr<FJsonValue>& PointValue : *Line)
		{
			const TArray<TSharedPtr<FJsonValue>>* Position = nullptr;
			FVector UeCm;
			if (PointValue.IsValid() && PointValue->TryGetArray(Position) && Position != nullptr && PositionToUeCm(*Position, OriginEastingM, OriginNorthingM, Epsg, UeCm))
			{
				Spline.PointsUeCm.Add(UeCm);
			}
		}
		if (Spline.PointsUeCm.Num() >= 2)
		{
			OutSplines.Add(MoveTemp(Spline));
		}
	}
}
}

bool FMantlePlaceRoadSplinesLogic::LonLatToUtm(
    double LonDeg, double LatDeg, int32 Epsg, double& OutEastingM, double& OutNorthingM)
{
	// UTM EPSG ranges: 32601-32660 (north) / 32701-32760 (south).
	const bool bNorth = Epsg >= 32601 && Epsg <= 32660;
	const bool bSouth = Epsg >= 32701 && Epsg <= 32760;
	if ((!bNorth && !bSouth) || LatDeg < -84.0 || LatDeg > 84.0 || LonDeg < -180.0 || LonDeg > 180.0)
	{
		return false;
	}
	const int32 Zone = Epsg - (bNorth ? 32600 : 32700);
	const double Lon0Rad = FMath::DegreesToRadians(-183.0 + 6.0 * Zone);

	const double LatRad = FMath::DegreesToRadians(LatDeg);
	const double LonRad = FMath::DegreesToRadians(LonDeg);

	const double E2 = Flattening * (2.0 - Flattening);
	const double Ep2 = E2 / (1.0 - E2);

	const double SinLat = FMath::Sin(LatRad);
	const double CosLat = FMath::Cos(LatRad);
	const double TanLat = FMath::Tan(LatRad);

	const double N = SemiMajorM / FMath::Sqrt(1.0 - E2 * SinLat * SinLat);
	const double T = TanLat * TanLat;
	const double C = Ep2 * CosLat * CosLat;
	const double A = (LonRad - Lon0Rad) * CosLat;

	// Meridional arc (Snyder eq. 3-21).
	const double M = SemiMajorM * ((1.0 - E2 / 4.0 - 3.0 * E2 * E2 / 64.0 - 5.0 * E2 * E2 * E2 / 256.0) * LatRad - (3.0 * E2 / 8.0 + 3.0 * E2 * E2 / 32.0 + 45.0 * E2 * E2 * E2 / 1024.0) * FMath::Sin(2.0 * LatRad) + (15.0 * E2 * E2 / 256.0 + 45.0 * E2 * E2 * E2 / 1024.0) * FMath::Sin(4.0 * LatRad) - (35.0 * E2 * E2 * E2 / 3072.0) * FMath::Sin(6.0 * LatRad));

	const double A2 = A * A;
	const double A3 = A2 * A;
	const double A4 = A3 * A;
	const double A5 = A4 * A;
	const double A6 = A5 * A;

	OutEastingM = FalseEastingM + K0 * N * (A + (1.0 - T + C) * A3 / 6.0 + (5.0 - 18.0 * T + T * T + 72.0 * C - 58.0 * Ep2) * A5 / 120.0);

	OutNorthingM = K0 * (M + N * TanLat * (A2 / 2.0 + (5.0 - T + 9.0 * C + 4.0 * C * C) * A4 / 24.0 + (61.0 - 58.0 * T + T * T + 600.0 * C - 330.0 * Ep2) * A6 / 720.0));
	if (bSouth)
	{
		OutNorthingM += SouthFalseNorthingM;
	}
	return true;
}

bool FMantlePlaceRoadSplinesLogic::ParseGeoJson(
    const FString& JsonText,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    FString& OutError)
{
	OutSplines.Reset();

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonText);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("RoadSplines.geojson is not valid JSON.");
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* Features = nullptr;
	if (!Root->TryGetArrayField(TEXT("features"), Features) || Features == nullptr)
	{
		OutError = TEXT("RoadSplines.geojson has no \"features\" array.");
		return false;
	}

	for (const TSharedPtr<FJsonValue>& FeatureValue : *Features)
	{
		const TSharedPtr<FJsonObject>* FeaturePtr = nullptr;
		if (!FeatureValue.IsValid() || !FeatureValue->TryGetObject(FeaturePtr) || FeaturePtr == nullptr)
		{
			continue; // one malformed feature must not drop the whole layer
		}
		const TSharedPtr<FJsonObject> Feature = *FeaturePtr;

		FMantlePlaceRoadSpline Prototype;
		const TSharedPtr<FJsonObject>* Properties = nullptr;
		if (Feature->TryGetObjectField(TEXT("properties"), Properties) && Properties != nullptr && Properties->IsValid())
		{
			(*Properties)->TryGetNumberField(TEXT("width_m_estimated"), Prototype.WidthMEstimated);
			(*Properties)->TryGetStringField(TEXT("class"), Prototype.RoadClass);
			(*Properties)->TryGetStringField(TEXT("name"), Prototype.Name);
		}

		const TSharedPtr<FJsonObject>* Geometry = nullptr;
		if (Feature->TryGetObjectField(TEXT("geometry"), Geometry) && Geometry != nullptr)
		{
			AppendGeometry(*Geometry, Prototype, OriginEastingM, OriginNorthingM, Epsg, OutSplines);
		}
	}
	return true;
}
