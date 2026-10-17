// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceFoliageScatterLogic.h"

namespace MantlePlaceFoliageScatterLogic
{
bool TexelAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, int32& OutColumn, int32& OutRow)
{
	if (!Raster.IsValid() || AoiSizeCm.X <= 0.0 || AoiSizeCm.Y <= 0.0)
	{
		return false;
	}
	const double HalfNorth = AoiSizeCm.X * 0.5;
	const double HalfEast = AoiSizeCm.Y * 0.5;
	if (FMath::Abs(PositionCm.X) > HalfNorth || FMath::Abs(PositionCm.Y) > HalfEast)
	{
		return false;
	}
	// North-up row-major: U runs west to east with +Y, V runs north to south against +X.
	const double U = (PositionCm.Y + HalfEast) / AoiSizeCm.Y;
	const double V = (HalfNorth - PositionCm.X) / AoiSizeCm.X;
	OutColumn = FMath::Clamp(FMath::FloorToInt32(U * Raster.Width), 0, Raster.Width - 1);
	OutRow = FMath::Clamp(FMath::FloorToInt32(V * Raster.Height), 0, Raster.Height - 1);
	return true;
}

bool RawAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, uint16& OutSample)
{
	int32 Column = 0, Row = 0;
	if (!TexelAt(Raster, AoiSizeCm, PositionCm, Column, Row))
	{
		return false;
	}
	OutSample = Raster.Samples[Row * Raster.Width + Column];
	return true;
}

bool DecodedScaleAt(const FRaster& Raster, const FVector2D& AoiSizeCm, const FVector& PositionCm, double& OutValue)
{
	uint16 Sample = 0;
	if (Raster.Mapping != EMantlePlaceCoverageMapping::Scale || Raster.FullScale <= 0.0
		|| !RawAt(Raster, AoiSizeCm, PositionCm, Sample))
	{
		return false;
	}
	if (Raster.bHasNodata && FMath::IsNearlyEqual(static_cast<double>(Sample), Raster.NodataValue))
	{
		return false;
	}
	OutValue = Raster.MinValue + (Raster.MaxValue - Raster.MinValue) * (static_cast<double>(Sample) / Raster.FullScale);
	return true;
}

bool InCell(const FVector& PositionCm, const FBox& CellBounds)
{
	return PositionCm.X >= CellBounds.Min.X && PositionCm.X < CellBounds.Max.X
		&& PositionCm.Y >= CellBounds.Min.Y && PositionCm.Y < CellBounds.Max.Y;
}

double Draw(const FVector& PositionCm, int32 Seed, int32 Salt)
{
	// Keyed on the point's position (whole centimetres), not its row: a re-import that reorders the
	// table must not reshuffle which trees a forest keeps.
	uint32 Hash = HashCombine(GetTypeHash(FMath::RoundToInt64(PositionCm.X)), GetTypeHash(FMath::RoundToInt64(PositionCm.Y)));
	Hash = HashCombine(Hash, GetTypeHash(Seed));
	Hash = HashCombine(Hash, GetTypeHash(Salt));
	// A final avalanche so neighbouring points do not draw neighbouring values.
	Hash ^= Hash >> 16;
	Hash *= 0x7feb352dU;
	Hash ^= Hash >> 15;
	Hash *= 0x846ca68bU;
	Hash ^= Hash >> 16;
	return static_cast<double>(Hash) / static_cast<double>(MAX_uint32);
}

EVerdict Judge(const FTreePoint& Point, const FInputs& Inputs, const FParams& Params, const FBox* CellBounds)
{
	const FVector& Position = Point.PositionCm;
	if (FMath::Abs(Position.X) > Inputs.AoiSizeCm.X * 0.5 || FMath::Abs(Position.Y) > Inputs.AoiSizeCm.Y * 0.5)
	{
		return EVerdict::OutsideAoi;
	}
	if (CellBounds != nullptr && !InCell(Position, *CellBounds))
	{
		return EVerdict::OutsideCell;
	}
	uint16 Sample = 0;
	if (RawAt(Inputs.WaterMask, Inputs.AoiSizeCm, Position, Sample)
		&& FMath::IsNearlyEqual(static_cast<double>(Sample), Inputs.WaterMask.TrueValue))
	{
		return EVerdict::Water;
	}
	if (RawAt(Inputs.LandCover, Inputs.AoiSizeCm, Position, Sample)
		&& Params.ExcludedLandCoverClasses.Contains(static_cast<int32>(Sample)))
	{
		return EVerdict::ExcludedLandCover;
	}
	double Keep = 1.0;
	double CanopyM = 0.0;
	if (!Point.bShrub && DecodedScaleAt(Inputs.CanopyHeight, Inputs.AoiSizeCm, Position, CanopyM))
	{
		if (CanopyM <= Params.MinCanopyHeightM)
		{
			return EVerdict::NoCanopy;
		}
		const double Span = Params.FullDensityCanopyHeightM - Params.MinCanopyHeightM;
		Keep = Span > 0.0 ? FMath::Clamp((CanopyM - Params.MinCanopyHeightM) / Span, 0.0, 1.0) : 1.0;
	}
	Keep *= FMath::Clamp(Params.DensityScale, 0.0, 1.0);
	if (Keep < 1.0 && Draw(Position, Params.Seed, 1) >= Keep)
	{
		return EVerdict::Thinned;
	}
	return EVerdict::Keep;
}

double TargetHeightCm(const FTreePoint& Point, const FInputs& Inputs)
{
	if (Point.HeightCm > 0.0f)
	{
		return Point.HeightCm;
	}
	double CanopyM = 0.0;
	if (DecodedScaleAt(Inputs.CanopyHeight, Inputs.AoiSizeCm, Point.PositionCm, CanopyM) && CanopyM > 0.0)
	{
		return CanopyM * 100.0;
	}
	return 0.0;
}

FTransform InstanceTransform(const FTreePoint& Point, double HeightCm, double GroundZCm, const FBox& MeshBounds, int32 Seed)
{
	const FVector Size = MeshBounds.GetSize();
	const double ScaleZ = Size.Z > UE_KINDA_SMALL_NUMBER ? HeightCm / Size.Z : 1.0;
	const double Width = FMath::Max(Size.X, Size.Y);
	const double ScaleXY = (Point.CrownRadiusCm > 0.0f && Width > UE_KINDA_SMALL_NUMBER)
		? (2.0 * Point.CrownRadiusCm) / Width
		: ScaleZ;
	const double Yaw = Draw(Point.PositionCm, Seed, 2) * 360.0;
	const FVector Location(Point.PositionCm.X, Point.PositionCm.Y, GroundZCm - MeshBounds.Min.Z * ScaleZ);
	return FTransform(FRotator(0.0, Yaw, 0.0), Location, FVector(ScaleXY, ScaleXY, ScaleZ));
}

int32 PickMesh(const FVector& PositionCm, int32 MeshCount, int32 Seed)
{
	if (MeshCount <= 0)
	{
		return INDEX_NONE;
	}
	return FMath::Min(MeshCount - 1, FMath::FloorToInt32(Draw(PositionCm, Seed, 3) * MeshCount));
}
}
