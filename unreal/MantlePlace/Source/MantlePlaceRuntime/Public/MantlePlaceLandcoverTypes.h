// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DataTable.h"
#include "MantlePlaceLandcoverTypes.generated.h"

/**
 * One detected tree instance from a bundle's Landcover/TreePoints.csv (columns:
 * x,y,ground_z,height_m,crown_radius_m — x/y are AOI-UTM meters, ground_z orthometric meters).
 * The importer converts positions into the Local Projected Frame (AOI centroid at world origin,
 * East -> +X, North -> +Y, 1 uu = 1 cm) and lands the rows in a UDataTable asset — PCG-ready
 * scatter input the user wires to their own foliage assets. Lives in the Runtime module so a
 * cooked build can still load DataTables that reference this row type.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceTreePointRow : public FTableRowBase
{
	GENERATED_BODY()

	/** Tree position in the Local Projected Frame (UE world cm; Z = ground elevation). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	FVector Position = FVector::ZeroVector;

	/** Canopy height in meters (CHM local maximum at this point). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float HeightM = 0.0f;

	/** Estimated crown radius in meters (0.35 x height, clamped 1-10 m by the ETL). */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float CrownRadiusM = 0.0f;

	/** Ground elevation under the point in orthometric meters; 0 when the DEM had no data there. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Landcover")
	float GroundZM = 0.0f;
};
