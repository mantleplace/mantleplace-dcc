// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DataTable.h"
#include "MantlePlaceLandcoverTypes.generated.h"

/**
 * What the platform classified a tree point as: the closed `foliage_type` vocabulary of the bundle
 * format (`spec/format.md` section 4.4). Never derived from a point's height or crown here; a value
 * this build does not know reads as `Tree`, because the vocabulary grows by gaining values and a
 * tree is what the layer is otherwise made of.
 */
UENUM(BlueprintType)
enum class EMantlePlaceFoliageType : uint8
{
	Tree  UMETA(DisplayName = "Tree"),
	Shrub UMETA(DisplayName = "Shrub"),
};

/**
 * One tree point of a bundle's tree-points CSV, as one row of the `DT_TreePoints` data table. The
 * property names below ARE the table's column contract: a PCG graph (or a Blueprint) binds to a
 * column by its property name, so a name here is renamed only by a deliberate, documented change.
 * `unreal/docs/foliage-table-contract.md` states each column's meaning and unit in prose, and
 * `FMantlePlaceTreePointsLogic::ColumnContract()` lists them for the test that holds the two
 * together.
 *
 * The importer converts positions into the Local Projected Frame (AOI centroid at world origin,
 * North -> +X, East -> +Y, 1 uu = 1 cm) and lands the rows in a UDataTable asset -- PCG-ready
 * scatter input the user wires to their own foliage assets. Lives in the Runtime module so a
 * cooked build can still load DataTables that reference this row type.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceTreePointRow : public FTableRowBase
{
	GENERATED_BODY()

	/**
	 * Tree position in the Local Projected Frame (UE world cm). Z is the ground elevation in cm when
	 * `bGroundKnown`, and 0 when it is not: read `bGroundKnown` before trusting Z.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	FVector Position = FVector::ZeroVector;

	/**
	 * What the platform classified this point as, from the CSV's `foliage_type` column. `Tree` for
	 * every point of a bundle that publishes no foliage-type vocabulary, and for any value the
	 * vocabulary this build knows does not contain.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	EMantlePlaceFoliageType FoliageType = EMantlePlaceFoliageType::Tree;

	/** Canopy height in meters: the CHM local maximum at this point, a height above ground, not an elevation. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float HeightM = 0.0f;

	/** `HeightM` in engine units (cm), so a scatter can scale by it without a unit conversion of its own. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float HeightCm = 0.0f;

	/** Crown radius in meters, as the platform published it (the ETL estimates it from the height and clamps it). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float CrownRadiusM = 0.0f;

	/** `CrownRadiusM` in engine units (cm). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float CrownRadiusCm = 0.0f;

	/** Ground elevation under the point in orthometric meters; 0 when the DEM had no data there (see `bGroundKnown`). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float GroundZM = 0.0f;

	/**
	 * False when the CSV's `ground_z` cell was empty, which the platform writes where the DEM had no
	 * data under the point. `GroundZM` and `Position.Z` are then 0 -- sea level, not a measurement --
	 * and a graph that snaps trees to the ground should project this point onto the landscape instead.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	bool bGroundKnown = false;
};
