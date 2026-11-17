// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceRoadSplinesLogic.h"

#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest::ProjectedToUeCm

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Serialization/JsonReader.h"
#include "Serialization/Csv/CsvParser.h"
#include "Serialization/JsonSerializer.h"

namespace
{
// WGS84 ellipsoid + UTM constants (USGS Professional Paper 1395, Snyder eq. 8-9..8-15).
constexpr double SemiMajorM = 6378137.0;
constexpr double Flattening = 1.0 / 298.257223563;
constexpr double K0 = 0.9996;
constexpr double FalseEastingM = 500000.0;
constexpr double SouthFalseNorthingM = 10000000.0;

// The vertical datum of every height this host places: its landscape, its mesh and its foliage,
// on every order. The format's own spelling of it, compared verbatim: one of the two values of the
// schema's `$defs/verticalDatum`, and the one `foliage_points.vertical_datum` is fixed to.
const TCHAR* const HostVerticalDatum = TEXT("EGM2008-orthometric");

/** Convert one WGS84 position (lon deg, lat deg, orthometric z m) into the Local Projected Frame (UE cm). */
bool LonLatZToUeCm(
    double LonDeg,
    double LatDeg,
    double ZM,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    FVector& OutUeCm)
{
	double EastingM = 0.0, NorthingM = 0.0;
	if (!FMantlePlaceRoadSplinesLogic::LonLatToUtm(LonDeg, LatDeg, Epsg, EastingM, NorthingM))
	{
		return false;
	}
	// Same frame math as FMantlePlaceVaultManifest::GetDrapeWorldRect / GetMeshLocation, through
	// the one helper that owns it: North -> +X, East -> +Y, orthometric metres -> +Z, all relative
	// to the AOI-centroid origin.
	OutUeCm = FMantlePlaceVaultManifest::ProjectedToUeCm(
	    EastingM - OriginEastingM, NorthingM - OriginNorthingM, ZM);
	return true;
}

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
	const double ZM = Position.Num() >= 3 ? Position[2]->AsNumber() : 0.0;
	return LonLatZToUeCm(Position[0]->AsNumber(), Position[1]->AsNumber(), ZM, OriginEastingM, OriginNorthingM, Epsg, OutUeCm);
}

/** Append every part of a LineString/MultiLineString geometry as its own spline. */
void AppendGeometry(
    const TSharedPtr<FJsonObject>& Geometry,
    const FMantlePlaceRoadSpline& Prototype,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen)
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
		++OutLinesSeen;
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

bool FMantlePlaceRoadSplinesLogic::CanPlaceHeights(const FMantlePlaceRoadSplinesDatum& Datum, FString& OutReason)
{
	OutReason.Reset();
	if (Datum.Stated.IsEmpty())
	{
		if (!Datum.bRequired)
		{
			return true; // a bundle from before the format stated a datum: every height was EGM2008
		}
		OutReason = FString::Printf(
		    TEXT("the road layer states no vertical datum for its heights, as a layer from manifest "
		         "1.8.0 on does wherever it carries them, and an unstated datum is never taken to be "
		         "%s, the datum of this landscape. The roads were not placed."),
		    HostVerticalDatum);
		return false;
	}
	if (Datum.Stated.Equals(HostVerticalDatum, ESearchCase::CaseSensitive))
	{
		return true;
	}
	OutReason = FString::Printf(
	    TEXT("the road layer states its heights in \"%s\", not %s, the datum of this landscape. "
	         "Placed as they are they would not sit on the ground, and this plugin converts no "
	         "height, so the roads were not placed."),
	    *Datum.Stated, HostVerticalDatum);
	return false;
}

bool FMantlePlaceRoadSplinesLogic::CanPlaceEngineHeights(const FString& StatedDatum, FString& OutReason)
{
	OutReason.Reset();
	if (StatedDatum.IsEmpty())
	{
		OutReason = FString::Printf(
		    TEXT("the engine-datum road copy (hosts.unreal.road_splines) states no vertical datum for its "
		         "heights, and an unstated datum is never taken to be %s, the datum of this landscape. "
		         "The copy was not read."),
		    HostVerticalDatum);
		return false;
	}
	if (StatedDatum.Equals(HostVerticalDatum, ESearchCase::CaseSensitive))
	{
		return true;
	}
	OutReason = FString::Printf(
	    TEXT("the engine-datum road copy (hosts.unreal.road_splines) states its heights in \"%s\", not %s, "
	         "the datum of this landscape. This plugin converts no height, so the copy was not read."),
	    *StatedDatum, HostVerticalDatum);
	return false;
}

TArray<FMantlePlaceRoadSplinesCandidate> FMantlePlaceRoadSplinesLogic::PlanSources(
    bool bEngineCopyDeclared,
    const FString& EngineStatedDatum,
    bool bSharedLayerDeclared,
    const FMantlePlaceRoadSplinesDatum& SharedDatum)
{
	TArray<FMantlePlaceRoadSplinesCandidate> Plan;
	if (bEngineCopyDeclared)
	{
		FMantlePlaceRoadSplinesCandidate Engine;
		Engine.Source = EMantlePlaceRoadSplinesSource::EngineCopy;
		Engine.bPlaceable = CanPlaceEngineHeights(EngineStatedDatum, Engine.Refusal);
		Plan.Add(MoveTemp(Engine));
	}
	if (bSharedLayerDeclared)
	{
		FMantlePlaceRoadSplinesCandidate Shared;
		Shared.Source = EMantlePlaceRoadSplinesSource::SharedLayer;
		Shared.bPlaceable = CanPlaceHeights(SharedDatum, Shared.Refusal);
		Plan.Add(MoveTemp(Shared));
	}
	return Plan;
}

const TCHAR* FMantlePlaceRoadSplinesLogic::SourceName(EMantlePlaceRoadSplinesSource Source)
{
	return Source == EMantlePlaceRoadSplinesSource::EngineCopy ? TEXT("engine") : TEXT("shared");
}

FString FMantlePlaceRoadSplinesLogic::DescribeSource(EMantlePlaceRoadSplinesSource Source, const FString& Path)
{
	if (Source == EMantlePlaceRoadSplinesSource::EngineCopy)
	{
		return FString::Printf(
		    TEXT("Road splines read from %s, the engine-datum copy (hosts.unreal.road_splines)."), *Path);
	}
	return FString::Printf(TEXT("Road splines read from %s."), *Path);
}

bool FMantlePlaceRoadSplinesLogic::IsUtmEpsg(int32 Epsg)
{
	// UTM EPSG ranges: 32601-32660 (north) / 32701-32760 (south).
	return (Epsg >= 32601 && Epsg <= 32660) || (Epsg >= 32701 && Epsg <= 32760);
}

bool FMantlePlaceRoadSplinesLogic::LonLatToUtm(
    double LonDeg, double LatDeg, int32 Epsg, double& OutEastingM, double& OutNorthingM)
{
	const bool bSouth = Epsg >= 32701; // meaningful only past the guard: IsUtmEpsg owns the ranges
	if (!IsUtmEpsg(Epsg) || LatDeg < -84.0 || LatDeg > 84.0 || LonDeg < -180.0 || LonDeg > 180.0)
	{
		return false;
	}
	const int32 Zone = Epsg - (bSouth ? 32700 : 32600);
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
    int32& OutLinesSeen,
    FString& OutError)
{
	OutSplines.Reset();
	OutLinesSeen = 0;

	if (!IsUtmEpsg(Epsg))
	{
		OutError = FString::Printf(
		    TEXT("the origin's EPSG:%d is not a UTM zone (32601-32660 or 32701-32760), so road centerlines "
		         "cannot be projected into the frame; the whole layer is refused rather than dropped point by point."),
		    Epsg);
		return false;
	}

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
			AppendGeometry(*Geometry, Prototype, OriginEastingM, OriginNorthingM, Epsg, OutSplines, OutLinesSeen);
		}
	}
	return true;
}

bool FMantlePlaceRoadSplinesLogic::AppendLonLatLine(
    const TArray<FVector>& LonLatZ,
    const FMantlePlaceRoadSpline& Prototype,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen)
{
	++OutLinesSeen;
	FMantlePlaceRoadSpline Spline = Prototype;
	for (const FVector& Position : LonLatZ)
	{
		FVector UeCm;
		if (LonLatZToUeCm(Position.X, Position.Y, Position.Z, OriginEastingM, OriginNorthingM, Epsg, UeCm))
		{
			Spline.PointsUeCm.Add(UeCm);
		}
	}
	if (Spline.PointsUeCm.Num() < 2)
	{
		return false;
	}
	OutSplines.Add(MoveTemp(Spline));
	return true;
}

FString FMantlePlaceRoadSplinesLogic::DescribeOutcome(int32 LinesSeen, int32 SplinesCreated)
{
	if (LinesSeen <= 0)
	{
		return TEXT("Road splines: the layer has no roads (0 spline actor(s)).");
	}
	if (SplinesCreated <= 0)
	{
		return FString::Printf(
		    TEXT("Road splines NOT placed: the layer has %d road line(s) but none could be placed "
		         "(each had fewer than two projectable points), so 0 spline actor(s) were created."),
		    LinesSeen);
	}
	if (SplinesCreated < LinesSeen)
	{
		return FString::Printf(
		    TEXT("Road splines created (%d spline actor(s)) from %d road line(s); %d dropped as unplaceable "
		         "(fewer than two projectable points)."),
		    SplinesCreated, LinesSeen, LinesSeen - SplinesCreated);
	}
	return FString::Printf(TEXT("Road splines created (%d spline actor(s))."), SplinesCreated);
}

bool FMantlePlaceRoadSplinesLogic::ParseSiteCsv(
    const FString& CsvText,
    double SiteHeightM,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen,
    int32& OutRowsUnreadable,
    FString& OutError)
{
	OutSplines.Reset();
	OutLinesSeen = 0;
	OutRowsUnreadable = 0;
	OutError.Reset();

	const FCsvParser Parser(CsvText);
	const FCsvParser::FRows& Rows = Parser.GetRows();
	if (Rows.Num() == 0)
	{
		OutError = TEXT("the road table has no header row.");
		return false;
	}

	// The columns are found by name in the header (format.md section 6), never by position.
	static const TCHAR* const Required[] = {
		TEXT("road"), TEXT("vertex"), TEXT("x"), TEXT("y"), TEXT("z"), TEXT("class"), TEXT("width_m") };
	TMap<FString, int32> Columns;
	for (int32 Index = 0; Index < Rows[0].Num(); ++Index)
	{
		Columns.Add(FString(Rows[0][Index]).TrimStartAndEnd().ToLower(), Index);
	}
	for (const TCHAR* Column : Required)
	{
		if (!Columns.Contains(Column))
		{
			OutError = FString::Printf(TEXT("the road table has no `%s` column."), Column);
			return false;
		}
	}
	const int32 RoadCol = Columns[TEXT("road")], VertexCol = Columns[TEXT("vertex")];
	const int32 XCol = Columns[TEXT("x")], YCol = Columns[TEXT("y")], ZCol = Columns[TEXT("z")];
	const int32 ClassCol = Columns[TEXT("class")], WidthCol = Columns[TEXT("width_m")];
	// `name` is optional: a 3.0 bundle's table has none, and a road with no name is left unnamed.
	const int32* NameColPtr = Columns.Find(TEXT("name"));
	const int32 NameCol = NameColPtr ? *NameColPtr : INDEX_NONE;
	int32 MaxCol = 0;
	for (const TPair<FString, int32>& Pair : Columns)
	{
		MaxCol = FMath::Max(MaxCol, Pair.Value);
	}

	const auto ReadNumber = [](const TCHAR* Field, double& Out)
	{
		const FString Text = FString(Field).TrimStartAndEnd();
		if (Text.IsEmpty() || !FCString::IsNumeric(*Text))
		{
			return false;
		}
		Out = FCString::Atod(*Text);
		return true;
	};

	// One row per vertex; the rows of one road share its `road` number and are in vertex order, so a
	// road ends where the number changes. Nothing is reordered or derived: the offsets are metres in
	// the local site frame and a height is metres above the site origin's height (format.md section 4).
	FMantlePlaceRoadSpline Current;
	FString CurrentRoad;
	bool bHaveRoad = false;
	const auto Flush = [&]()
	{
		if (!bHaveRoad)
		{
			return;
		}
		++OutLinesSeen;
		if (Current.PointsUeCm.Num() >= 2)
		{
			OutSplines.Add(MoveTemp(Current));
		}
		Current = FMantlePlaceRoadSpline();
		bHaveRoad = false;
	};
	for (int32 RowIndex = 1; RowIndex < Rows.Num(); ++RowIndex)
	{
		const TArray<const TCHAR*>& Row = Rows[RowIndex];
		if (Row.Num() == 1 && FString(Row[0]).TrimStartAndEnd().IsEmpty())
		{
			continue; // a blank line
		}
		double X = 0.0, Y = 0.0, Z = 0.0;
		if (Row.Num() <= MaxCol || !ReadNumber(Row[XCol], X) || !ReadNumber(Row[YCol], Y) || !ReadNumber(Row[ZCol], Z))
		{
			++OutRowsUnreadable;
			continue;
		}
		const FString Road = FString(Row[RoadCol]).TrimStartAndEnd();
		if (!bHaveRoad || Road != CurrentRoad)
		{
			Flush();
			CurrentRoad = Road;
			bHaveRoad = true;
			double Width = 0.0;
			Current.WidthMEstimated = ReadNumber(Row[WidthCol], Width) ? Width : 0.0;
			Current.RoadClass = FString(Row[ClassCol]).TrimStartAndEnd();
			Current.Name = NameCol == INDEX_NONE ? FString() : FString(Row[NameCol]);
		}
		Current.PointsUeCm.Add(FMantlePlaceVaultManifest::ProjectedToUeCm(X, Y, SiteHeightM + Z));
	}
	Flush();
	return true;
}
