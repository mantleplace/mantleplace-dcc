// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceCuratorExtentLogic.h"

#include "MantlePlaceAuthLogic.h"  // IsValidBaseUrl
#include "MantlePlaceVaultLogic.h" // NormalizeBaseUrl

namespace
{
	/** The curator keeps four decimals (about 11 m); a link carries no more than it keeps. */
	constexpr double BoundScale = 10000.0;

	/** Added before a floor / subtracted before a ceil, so 12.3456 stays 12.3456 despite binary error. */
	constexpr double RoundingSlack = 1e-6;

	/** A latitude this close to a pole has no east-west metre worth the name. */
	constexpr double MaxBoxLatitude = 89.9;

	bool IsFiniteDegrees(double Longitude, double Latitude)
	{
		return FMath::IsFinite(Longitude) && FMath::IsFinite(Latitude)
			&& Longitude >= -180.0 && Longitude <= 180.0
			&& Latitude >= -90.0 && Latitude <= 90.0;
	}

	/** Metres in one degree of latitude at this latitude (WGS84, the standard series). */
	double MetresPerDegreeLatitude(double LatitudeDeg)
	{
		const double Phi = FMath::DegreesToRadians(LatitudeDeg);
		return 111132.954 - 559.822 * FMath::Cos(2.0 * Phi) + 1.175 * FMath::Cos(4.0 * Phi);
	}

	/** Metres in one degree of longitude at this latitude (WGS84, the standard series). */
	double MetresPerDegreeLongitude(double LatitudeDeg)
	{
		const double Phi = FMath::DegreesToRadians(LatitudeDeg);
		return 111412.84 * FMath::Cos(Phi) - 93.5 * FMath::Cos(3.0 * Phi) + 0.118 * FMath::Cos(5.0 * Phi);
	}

	void Extend(FMantlePlaceExtent& Into, const FMantlePlaceExtent& Other, bool& bAny)
	{
		if (!bAny)
		{
			Into = Other;
			bAny = true;
			return;
		}
		Into.West = FMath::Min(Into.West, Other.West);
		Into.South = FMath::Min(Into.South, Other.South);
		Into.East = FMath::Max(Into.East, Other.East);
		Into.North = FMath::Max(Into.North, Other.North);
	}

	bool FromGeoreferencedPoints(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent)
	{
		if (Points.Num() < 2)
		{
			return false;
		}
		FMantlePlaceExtent Box;
		Box.West = Box.East = Points[0].Longitude;
		Box.South = Box.North = Points[0].Latitude;
		for (const FMantlePlaceGeoPoint& Point : Points)
		{
			if (!IsFiniteDegrees(Point.Longitude, Point.Latitude))
			{
				return false;
			}
			Box.West = FMath::Min(Box.West, Point.Longitude);
			Box.East = FMath::Max(Box.East, Point.Longitude);
			Box.South = FMath::Min(Box.South, Point.Latitude);
			Box.North = FMath::Max(Box.North, Point.Latitude);
		}
		// A level that straddles the antimeridian reads as nearly the whole world west to east. The
		// curator wants West < East, so it is refused rather than drawn across the wrong way round.
		if (!Box.IsValid() || (Box.East - Box.West) > 180.0)
		{
			return false;
		}
		OutExtent = Box;
		return true;
	}
}

bool FMantlePlaceExtent::IsValid() const
{
	return FMath::IsFinite(West) && FMath::IsFinite(South) && FMath::IsFinite(East) && FMath::IsFinite(North)
		&& West >= -180.0 && East <= 180.0 && South >= -90.0 && North <= 90.0
		&& West < East && South < North;
}

const TCHAR* FMantlePlaceCuratorExtentLogic::From()
{
	return TEXT("unreal");
}

bool FMantlePlaceCuratorExtentLogic::FromPriorImports(const TArray<FMantlePlaceExtent>& ImportExtents, FMantlePlaceExtent& OutExtent)
{
	FMantlePlaceExtent Union;
	bool bAny = false;
	for (const FMantlePlaceExtent& Extent : ImportExtents)
	{
		if (Extent.IsValid())
		{
			Extend(Union, Extent, bAny);
		}
	}
	if (!bAny)
	{
		return false;
	}
	OutExtent = Union;
	return true;
}

bool FMantlePlaceCuratorExtentLogic::FromNativeGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent)
{
	return FromGeoreferencedPoints(Points, OutExtent);
}

bool FMantlePlaceCuratorExtentLogic::FromCesiumGeoreference(const TArray<FMantlePlaceGeoPoint>& Points, FMantlePlaceExtent& OutExtent)
{
	return FromGeoreferencedPoints(Points, OutExtent);
}

bool FMantlePlaceCuratorExtentLogic::FromExtentBox(const FMantlePlaceExtentBoxState& Box, FMantlePlaceExtent& OutExtent)
{
	if (!IsFiniteDegrees(Box.CenterLongitude, Box.CenterLatitude)
		|| !FMath::IsFinite(Box.WidthM) || !FMath::IsFinite(Box.DepthM)
		|| Box.WidthM <= 0.0 || Box.DepthM <= 0.0
		|| FMath::Abs(Box.CenterLatitude) > MaxBoxLatitude
		|| (Box.CenterLongitude == 0.0 && Box.CenterLatitude == 0.0))
	{
		return false;
	}

	const double HalfLatitude = (Box.DepthM * 0.5) / MetresPerDegreeLatitude(Box.CenterLatitude);
	const double HalfLongitude = (Box.WidthM * 0.5) / MetresPerDegreeLongitude(Box.CenterLatitude);

	FMantlePlaceExtent Extent;
	Extent.West = Box.CenterLongitude - HalfLongitude;
	Extent.East = Box.CenterLongitude + HalfLongitude;
	Extent.South = Box.CenterLatitude - HalfLatitude;
	Extent.North = Box.CenterLatitude + HalfLatitude;
	// A box that leaves the world (across the antimeridian or past a pole) has no W,S,E,N the
	// curator can draw; it is refused rather than clipped to a smaller box than the user placed.
	if (!Extent.IsValid())
	{
		return false;
	}
	OutExtent = Extent;
	return true;
}

bool FMantlePlaceCuratorExtentLogic::FromExtentBoxes(const TArray<FMantlePlaceExtentBoxState>& Boxes, FMantlePlaceExtent& OutExtent)
{
	FMantlePlaceExtent Union;
	bool bAny = false;
	for (const FMantlePlaceExtentBoxState& Box : Boxes)
	{
		FMantlePlaceExtent One;
		if (FromExtentBox(Box, One))
		{
			Extend(Union, One, bAny);
		}
	}
	if (!bAny)
	{
		return false;
	}
	OutExtent = Union;
	return true;
}

FMantlePlaceResolvedExtent FMantlePlaceCuratorExtentLogic::Resolve(const FMantlePlaceLevelState& Level)
{
	FMantlePlaceResolvedExtent Resolved;
	if (FromPriorImports(Level.ImportExtents, Resolved.Extent))
	{
		Resolved.Source = EMantlePlaceExtentSource::PriorImport;
	}
	else if (FromNativeGeoreference(Level.NativeGeoreferencedPoints, Resolved.Extent))
	{
		Resolved.Source = EMantlePlaceExtentSource::NativeGeoreference;
	}
	else if (FromCesiumGeoreference(Level.CesiumGeoreferencedPoints, Resolved.Extent))
	{
		Resolved.Source = EMantlePlaceExtentSource::CesiumGeoreference;
	}
	else if (FromExtentBoxes(Level.ExtentBoxes, Resolved.Extent))
	{
		Resolved.Source = EMantlePlaceExtentSource::ExtentBox;
	}
	return Resolved;
}

FString FMantlePlaceCuratorExtentLogic::DescribeSource(EMantlePlaceExtentSource Source)
{
	switch (Source)
	{
	case EMantlePlaceExtentSource::PriorImport:
		return TEXT("a previous Mantle Place import");
	case EMantlePlaceExtentSource::NativeGeoreference:
		return TEXT("the level's georeferencing");
	case EMantlePlaceExtentSource::CesiumGeoreference:
		return TEXT("the level's Cesium georeference");
	case EMantlePlaceExtentSource::ExtentBox:
		return TEXT("the Extent box");
	case EMantlePlaceExtentSource::None:
		break;
	}
	return TEXT("nothing in the level");
}

FString FMantlePlaceCuratorExtentLogic::FormatBound(double Value, bool bRoundUp)
{
	const double Scaled = Value * BoundScale;
	const double Rounded = bRoundUp ? FMath::CeilToDouble(Scaled - RoundingSlack) : FMath::FloorToDouble(Scaled + RoundingSlack);
	const int64 Units = static_cast<int64>(Rounded);
	const int64 Whole = Units / 10000;
	const int64 Fraction = FMath::Abs(Units % 10000);

	FString Text = FString::Printf(TEXT("%lld"), Whole);
	if (Fraction != 0)
	{
		FString Digits = FString::Printf(TEXT("%04lld"), Fraction);
		while (Digits.EndsWith(TEXT("0")))
		{
			Digits.LeftChopInline(1);
		}
		// Whole is 0 for -0.5, which would print as 0.5: the sign has to be put back by hand.
		Text = ((Units < 0 && Whole == 0) ? FString(TEXT("-0")) : Text) + TEXT(".") + Digits;
	}
	return Text;
}

FString FMantlePlaceCuratorExtentLogic::FormatToken(const FMantlePlaceExtent& Extent)
{
	return FormatBound(Extent.West, false) + TEXT(",") + FormatBound(Extent.South, false) + TEXT(",")
		+ FormatBound(Extent.East, true) + TEXT(",") + FormatBound(Extent.North, true);
}

FString FMantlePlaceCuratorExtentLogic::BuildCuratorUrl(const FString& BaseUrl, const FMantlePlaceResolvedExtent& Resolved)
{
	if (!FMantlePlaceAuthLogic::IsValidBaseUrl(BaseUrl))
	{
		return FString();
	}
	FString Url = FMantlePlaceVaultLogic::NormalizeBaseUrl(BaseUrl) + TEXT("/curator");
	if (Resolved.IsSet() && Resolved.Extent.IsValid())
	{
		// Digits, minus signs, dots and commas only: nothing here needs percent-encoding.
		Url += TEXT("#aoi=") + FormatToken(Resolved.Extent) + TEXT("&from=") + From();
	}
	return Url;
}
