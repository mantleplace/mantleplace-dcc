// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "PCGElement.h"
#include "PCGSettings.h"

#include "MantlePlaceTreeScatterSettings.generated.h"

class UStaticMesh;
class UTexture2D;

/**
 * The tree-points columns the scatter binds by name: the contract's own names, which
 * `MantlePlace.Import.FoliageScatter.Columns` holds against FMantlePlaceTreePointsLogic::ColumnContract.
 */
namespace MantlePlaceTreeScatterColumns
{
inline const TCHAR* Position() { return TEXT("Position"); }
inline const TCHAR* FoliageType() { return TEXT("FoliageType"); }
inline const TCHAR* HeightCm() { return TEXT("HeightCm"); }
inline const TCHAR* CrownRadiusCm() { return TEXT("CrownRadiusCm"); }
inline const TCHAR* GroundKnown() { return TEXT("bGroundKnown"); }
}

/**
 * The Mantle Place tree scatter: the one node of the foliage graph that is ours.
 *
 * Its input is the tree-points table as a Load Data Table node reads it, one point per row, and it
 * binds the columns by the names `unreal/docs/foliage-table-contract.md` documents (`Position`,
 * `FoliageType`, `HeightCm`, `CrownRadiusCm`, `bGroundKnown`). It drops the points that stand on
 * water (`water_mask`), on an excluded land-cover class (`worldcover`), or -- for trees -- under no
 * canopy (`canopy_height`, decoded by its published value mapping, which also thins a sparse canopy
 * and stands in for a height the row does not publish). The survivors leave as points carrying their
 * instance transform and a `Mesh` attribute that a Static Mesh Spawner reads by attribute.
 *
 * It emits only the points inside the execution bounds, so on a partitioned component each World
 * Partition cell generates its own trees and nothing else. The rasters are read from the textures'
 * source data, which exists in the editor only: generation is an editor step, and its instances are
 * what a cooked build ships.
 */
UCLASS(BlueprintType, ClassGroup = (Procedural))
class MANTLEPLACERUNTIME_API UMantlePlaceTreeScatterSettings : public UPCGSettings
{
	GENERATED_BODY()

public:
	/** The canopy-height coverage raster: thins trees under a thin canopy and drops them under none. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Rasters")
	TSoftObjectPtr<UTexture2D> CanopyHeight;

	/** The water-mask coverage raster: no point stands where it is set. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Rasters")
	TSoftObjectPtr<UTexture2D> WaterMask;

	/** The land-cover (worldcover) coverage raster: no point stands on an excluded class. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Rasters")
	TSoftObjectPtr<UTexture2D> LandCover;

	/** The AOI's span in cm, (North, East), centred on the origin: the extent the rasters cover. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Rasters")
	FVector2D AoiSizeCm = FVector2D::ZeroVector;

	/** The meshes a tree is drawn with, one picked per point. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Meshes")
	TArray<TSoftObjectPtr<UStaticMesh>> TreeMeshes;

	/** The meshes a shrub is drawn with. Empty falls back to TreeMeshes. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Meshes")
	TArray<TSoftObjectPtr<UStaticMesh>> ShrubMeshes;

	/** The attribute the chosen mesh is written to, which the spawner selects by. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Meshes")
	FName MeshAttribute = FName(TEXT("Mesh"));

	/** Land-cover class codes no point stands on. Defaults: 50 built-up, 70 snow and ice, 80 permanent water. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Scatter")
	TArray<int32> ExcludedLandCoverClasses = { 50, 70, 80 };

	/** At or below this canopy height (m) a tree point is dropped. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Scatter", meta = (ClampMin = "0"))
	double MinCanopyHeightM = 0.5;

	/** At or above this canopy height (m) every tree point is kept; between the two, in proportion. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Scatter", meta = (ClampMin = "0"))
	double FullDensityCanopyHeightM = 3.0;

	/** Scales the scatter's density, 0 to 1. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Scatter", meta = (ClampMin = "0", ClampMax = "1"))
	double DensityScale = 1.0;

	/** Seeds the thinning, the mesh choice and the yaw. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Scatter")
	int32 ScatterSeed = 0;

	//~Begin UPCGSettings interface
#if WITH_EDITOR
	virtual FName GetDefaultNodeName() const override { return FName(TEXT("MantlePlaceTreeScatter")); }
	virtual FText GetDefaultNodeTitle() const override { return NSLOCTEXT("MantlePlaceTreeScatter", "NodeTitle", "Mantle Place Tree Scatter"); }
	virtual FText GetNodeTooltipText() const override;
	virtual EPCGSettingsType GetType() const override { return EPCGSettingsType::Sampler; }
#endif

protected:
	virtual TArray<FPCGPinProperties> InputPinProperties() const override { return Super::DefaultPointInputPinProperties(); }
	virtual TArray<FPCGPinProperties> OutputPinProperties() const override { return Super::DefaultPointOutputPinProperties(); }
	virtual FPCGElementPtr CreateElement() const override;
	//~End UPCGSettings interface
};

class FMantlePlaceTreeScatterElement : public IPCGElement
{
public:
	// The rasters and the meshes are UObjects read from their source data: the game thread only.
	virtual bool CanExecuteOnlyOnMainThread(FPCGContext* Context) const override { return true; }
	// A re-import replaces the textures under the same names; a cached result would outlive them.
	virtual bool IsCacheable(const UPCGSettings* InSettings) const override { return false; }

protected:
	virtual bool ExecuteInternal(FPCGContext* Context) const override;
};
