// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceFoliageScatterLogic.h"

// The scatter's decisions, without an engine world: which texel a point reads, how a published
// mapping decodes, and which points survive the water mask, the land cover and the canopy.

namespace
{
using namespace MantlePlaceFoliageScatterLogic;

// A 4x4 raster over a 400 m x 400 m AOI: each texel is 100 m square. Row 0 is the northern edge.
const FVector2D Aoi(40000.0, 40000.0);

FRaster MakeRaster(EMantlePlaceCoverageMapping Mapping, uint16 Fill)
{
	FRaster Raster;
	Raster.Width = 4;
	Raster.Height = 4;
	Raster.Samples.Init(Fill, 16);
	Raster.Mapping = Mapping;
	return Raster;
}

// The local-frame centre of texel (column, row): North is +X, East is +Y, row 0 is north.
FVector CentreOf(int32 Column, int32 Row)
{
	return FVector(20000.0 - (Row + 0.5) * 10000.0, -20000.0 + (Column + 0.5) * 10000.0, 0.0);
}

FTreePoint Tree(const FVector& Position, float HeightCm = 1500.0f, float CrownCm = 300.0f)
{
	FTreePoint Point;
	Point.PositionCm = Position;
	Point.HeightCm = HeightCm;
	Point.CrownRadiusCm = CrownCm;
	Point.bGroundKnown = true;
	return Point;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFoliageScatterTexelTest,
	"MantlePlace.Import.FoliageScatterLogic.Texel",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FMantlePlaceFoliageScatterTexelTest::RunTest(const FString& Parameters)
{
	const FRaster Raster = MakeRaster(EMantlePlaceCoverageMapping::Mask, 0);
	int32 Column = -1, Row = -1;

	TestTrue(TEXT("the north-west corner texel"), TexelAt(Raster, Aoi, CentreOf(0, 0), Column, Row));
	TestEqual(TEXT("north-west: column 0"), Column, 0);
	TestEqual(TEXT("north-west: row 0"), Row, 0);

	// North is +X and maps to the TOP row; East is +Y and maps to the right-hand column. A transposed
	// lookup reads (3, 0) here as (0, 3), which is the silent error this pins.
	TestTrue(TEXT("the north-east corner texel"), TexelAt(Raster, Aoi, CentreOf(3, 0), Column, Row));
	TestEqual(TEXT("north-east: column 3"), Column, 3);
	TestEqual(TEXT("north-east: row 0"), Row, 0);
	TestTrue(TEXT("a point south of centre"), TexelAt(Raster, Aoi, FVector(-15000.0, -15000.0, 0.0), Column, Row));
	TestEqual(TEXT("south-west: row 3"), Row, 3);
	TestEqual(TEXT("south-west: column 0"), Column, 0);

	TestTrue(TEXT("the far edge clamps into the last texel"), TexelAt(Raster, Aoi, FVector(-20000.0, 20000.0, 0.0), Column, Row));
	TestEqual(TEXT("edge: column 3"), Column, 3);
	TestEqual(TEXT("edge: row 3"), Row, 3);
	TestFalse(TEXT("outside the AOI reads nothing"), TexelAt(Raster, Aoi, FVector(20001.0, 0.0, 0.0), Column, Row));
	TestFalse(TEXT("an empty raster reads nothing"), TexelAt(FRaster(), Aoi, FVector::ZeroVector, Column, Row));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFoliageScatterDecodeTest,
	"MantlePlace.Import.FoliageScatterLogic.Decode",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FMantlePlaceFoliageScatterDecodeTest::RunTest(const FString& Parameters)
{
	FRaster Canopy = MakeRaster(EMantlePlaceCoverageMapping::Scale, 0);
	Canopy.MinValue = 0.0;
	Canopy.MaxValue = 60.0;
	Canopy.Samples[0] = 65535;  // (0, 0): full scale, 60 m
	Canopy.Samples[1] = 32768;  // (1, 0): about half, 30 m
	Canopy.bHasNodata = true;
	Canopy.NodataValue = 7.0;
	Canopy.Samples[2] = 7;      // (2, 0): the nodata sentinel

	double Value = 0.0;
	TestTrue(TEXT("full scale decodes"), DecodedScaleAt(Canopy, Aoi, CentreOf(0, 0), Value));
	TestEqual(TEXT("full scale is MaxValue"), Value, 60.0, 1e-6);
	TestTrue(TEXT("mid scale decodes"), DecodedScaleAt(Canopy, Aoi, CentreOf(1, 0), Value));
	TestEqual(TEXT("mid scale is linear"), Value, 60.0 * 32768.0 / 65535.0, 1e-6);
	TestFalse(TEXT("the nodata sample is not a value"), DecodedScaleAt(Canopy, Aoi, CentreOf(2, 0), Value));

	const FRaster Legend = MakeRaster(EMantlePlaceCoverageMapping::Legend, 10);
	TestFalse(TEXT("a legend is never decoded as a ramp"), DecodedScaleAt(Legend, Aoi, CentreOf(0, 0), Value));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFoliageScatterJudgeTest,
	"MantlePlace.Import.FoliageScatterLogic.Judge",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FMantlePlaceFoliageScatterJudgeTest::RunTest(const FString& Parameters)
{
	FInputs Inputs;
	Inputs.AoiSizeCm = Aoi;
	Inputs.WaterMask = MakeRaster(EMantlePlaceCoverageMapping::Mask, 0);
	Inputs.WaterMask.TrueValue = 255.0;
	Inputs.WaterMask.FullScale = 255.0;
	Inputs.WaterMask.Samples[5] = 255; // (1, 1) is water
	Inputs.LandCover = MakeRaster(EMantlePlaceCoverageMapping::Legend, 10);
	Inputs.LandCover.FullScale = 255.0;
	Inputs.LandCover.Samples[6] = 50;  // (2, 1) is built-up
	Inputs.CanopyHeight = MakeRaster(EMantlePlaceCoverageMapping::Scale, 65535);
	Inputs.CanopyHeight.MaxValue = 30.0;
	Inputs.CanopyHeight.Samples[9] = 0; // (1, 2) has no canopy
	const FParams Params;

	TestEqual(TEXT("a tree on open forest is kept"), Judge(Tree(CentreOf(0, 0)), Inputs, Params, nullptr), EVerdict::Keep);
	TestEqual(TEXT("a tree on water is dropped"), Judge(Tree(CentreOf(1, 1)), Inputs, Params, nullptr), EVerdict::Water);
	TestEqual(TEXT("a tree on built-up land is dropped"), Judge(Tree(CentreOf(2, 1)), Inputs, Params, nullptr), EVerdict::ExcludedLandCover);
	TestEqual(TEXT("a tree under no canopy is dropped"), Judge(Tree(CentreOf(1, 2)), Inputs, Params, nullptr), EVerdict::NoCanopy);
	FTreePoint Shrub = Tree(CentreOf(1, 2), 150.0f, 80.0f);
	Shrub.bShrub = true;
	TestEqual(TEXT("a shrub is not judged by the canopy"), Judge(Shrub, Inputs, Params, nullptr), EVerdict::Keep);
	TestEqual(TEXT("a point outside the AOI is dropped"), Judge(Tree(FVector(30000.0, 0.0, 0.0)), Inputs, Params, nullptr), EVerdict::OutsideAoi);

	// The partition cell: a point on a shared border belongs to exactly one of the two cells.
	const FBox West(FVector(-20000.0, -20000.0, -1e6), FVector(20000.0, 0.0, 1e6));
	const FBox East(FVector(-20000.0, 0.0, -1e6), FVector(20000.0, 20000.0, 1e6));
	const FTreePoint OnBorder = Tree(FVector(15000.0, 0.0, 0.0));
	const int32 Claims = (Judge(OnBorder, Inputs, Params, &West) == EVerdict::Keep ? 1 : 0)
		+ (Judge(OnBorder, Inputs, Params, &East) == EVerdict::Keep ? 1 : 0);
	TestEqual(TEXT("a border point is in exactly one cell"), Claims, 1);
	TestEqual(TEXT("a point in the other cell is outside this one"), Judge(Tree(CentreOf(0, 0)), Inputs, Params, &East), EVerdict::OutsideCell);

	// Thinning: zero density drops every point, and the draw is deterministic.
	FParams None = Params;
	None.DensityScale = 0.0;
	TestEqual(TEXT("zero density keeps nothing"), Judge(Tree(CentreOf(0, 0)), Inputs, None, nullptr), EVerdict::Thinned);
	TestEqual(TEXT("the draw is deterministic"), Draw(CentreOf(3, 3), 7, 1), Draw(CentreOf(3, 3), 7, 1));

	// Missing rasters filter nothing: the table's point stands.
	FInputs Bare;
	Bare.AoiSizeCm = Aoi;
	TestEqual(TEXT("no rasters: the point stands"), Judge(Tree(CentreOf(1, 1)), Bare, Params, nullptr), EVerdict::Keep);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFoliageScatterInstanceTest,
	"MantlePlace.Import.FoliageScatterLogic.Instance",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FMantlePlaceFoliageScatterInstanceTest::RunTest(const FString& Parameters)
{
	FInputs Inputs;
	Inputs.AoiSizeCm = Aoi;
	Inputs.CanopyHeight = MakeRaster(EMantlePlaceCoverageMapping::Scale, 32768);
	Inputs.CanopyHeight.MaxValue = 40.0;

	TestEqual(TEXT("the table's height wins"), TargetHeightCm(Tree(CentreOf(0, 0), 1200.0f), Inputs), 1200.0, 1e-3);
	TestEqual(TEXT("a row with no height takes the canopy's"),
		TargetHeightCm(Tree(CentreOf(0, 0), 0.0f), Inputs), 100.0 * 40.0 * 32768.0 / 65535.0, 1e-3);
	TestEqual(TEXT("neither: zero"), TargetHeightCm(Tree(CentreOf(0, 0), 0.0f), FInputs()), 0.0, 1e-9);

	// The engine cone: 100 cm tall, 100 cm wide, pivot at its centre.
	const FBox Cone(FVector(-50.0, -50.0, -50.0), FVector(50.0, 50.0, 50.0));
	const FTreePoint Point = Tree(FVector(100.0, 200.0, 5000.0), 1500.0f, 300.0f);
	const FTransform Transform = InstanceTransform(Point, 1500.0, 5000.0, Cone, 0);
	TestEqual(TEXT("height scale"), Transform.GetScale3D().Z, 15.0, 1e-6);
	TestEqual(TEXT("crown: width is the crown's diameter"), Transform.GetScale3D().X, 6.0, 1e-6);
	TestEqual(TEXT("the base sits on the ground"), Transform.GetLocation().Z, 5000.0 + 50.0 * 15.0, 1e-6);
	TestEqual(TEXT("at the point"), FVector2D(Transform.GetLocation()), FVector2D(100.0, 200.0));

	const FTransform NoCrown = InstanceTransform(Tree(FVector::ZeroVector, 1000.0f, 0.0f), 1000.0, 0.0, Cone, 0);
	TestEqual(TEXT("no crown: uniform scale"), NoCrown.GetScale3D().X, NoCrown.GetScale3D().Z, 1e-6);

	TestEqual(TEXT("no meshes: none"), PickMesh(FVector::ZeroVector, 0, 0), INDEX_NONE);
	const int32 Pick = PickMesh(FVector(123.0, 456.0, 0.0), 3, 9);
	TestTrue(TEXT("a pick is in range"), Pick >= 0 && Pick < 3);
	TestEqual(TEXT("a pick is deterministic"), PickMesh(FVector(123.0, 456.0, 0.0), 3, 9), Pick);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
