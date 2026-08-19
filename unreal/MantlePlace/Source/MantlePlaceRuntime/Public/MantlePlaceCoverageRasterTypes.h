// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Engine/AssetUserData.h"
#include "MantlePlaceCoverageRasterTypes.generated.h"

/**
 * How a coverage raster's pixels turn back into meaning. The four kinds are NOT interchangeable
 * ramps, which is the whole reason this is an enum rather than a bag of optional fields: reading
 * MinValue/MaxValue off a Legend would treat ESA class codes as a scale and silently invent a
 * gradient between "grassland" and "built-up".
 */
UENUM(BlueprintType)
enum class EMantlePlaceCoverageMapping : uint8
{
	/** Continuous values on a linear scale — `min`/`max`/`to_value`, usually with Units. */
	Scale,

	/** Categorical class codes that ARE the legend; never rescaled, never interpolated. */
	Legend,

	/** Two-valued: TrueValue where the mask is set, FalseValue where it is not. */
	Mask,

	/** Already 8-bit and copied through verbatim (`identity: true`); the pixel IS the value. */
	Identity,
};

/**
 * The meaning and provenance of one imported coverage raster, attached to its UTexture2D.
 *
 * A coverage raster describes what is ON the ground without changing what the Landscape IS — that
 * is what separates it from a weightmap layer (see CONTEXT.md). Without this payload the texture is
 * an anonymous 0-65535 ramp: nothing on a UTexture2D records that `slope` is degrees, that
 * `worldcover` is a legend, or which bundle the bytes came from.
 *
 * It lives in the RUNTIME module deliberately. UAssetUserData is serialized into the texture, so a
 * payload class defined in an editor-only module would fail to load in a cooked build — the exact
 * disappears-at-cook failure that ruled out asset metadata tags, arriving by a different route and
 * only visible after packaging.
 */
UCLASS(BlueprintType)
class MANTLEPLACERUNTIME_API UMantlePlaceCoverageRasterData : public UAssetUserData
{
	GENERATED_BODY()

public:
	/** Which mapping applies. Read this BEFORE any value field — the others are kind-specific. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster")
	EMantlePlaceCoverageMapping Mapping = EMantlePlaceCoverageMapping::Identity;

	/** The manifest's own layer key, e.g. "ndvi", "worldcover", "canopy_height". */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster")
	FString LayerName;

	/** The `ue_ready[].encoding` this texture was imported under, e.g. "png-16bit-grayscale". */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster")
	FString Encoding;

	// --- Scale ------------------------------------------------------------------------------

	/** Scale only: the real-world value at sample 0. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	double MinValue = 0.0;

	/** Scale only: the real-world value at the encoding's full-scale sample. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	double MaxValue = 0.0;

	/** Scale only: the ETL's own formula, carried verbatim so the decode is auditable. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	FString ToValueFormula;

	/** Scale only: "m", "degrees", … Empty when the layer is unitless (e.g. ndvi). */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	FString Units;

	/** Scale only: whether NodataValue is meaningful. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	bool bHasNodata = false;

	/**
	 * Scale only: the sample that means "no data". Textures are imported unfiltered and un-mipped,
	 * but a consumer that adds its own filtering must exclude this value itself — averaging a
	 * nodata sentinel with real samples produces a plausible wrong number, not a visible hole.
	 */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Scale")
	double NodataValue = 0.0;

	// --- Legend / Mask ----------------------------------------------------------------------

	/** Legend only: the class codes present, e.g. the ESA WorldCover 10/20/30/50/60/80 set. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Legend")
	TArray<int32> Classes;

	/** Mask only: the value meaning "set". */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Mask")
	double TrueValue = 0.0;

	/** Mask only: the value meaning "not set". */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Mask")
	double FalseValue = 0.0;

	// --- Provenance -------------------------------------------------------------------------
	//
	// An imported texture is a derived copy of a hash-verified bundle artifact. Integrity
	// Inheritance (CONTEXT.md) says its trust comes from the digest of the bundle that carried
	// it — so a texture with no record of WHICH bundle that was cannot inherit anything. These
	// cannot drift from the pixels: a re-import rewrites both in the same operation.

	/** The in-zip path the pixels came from, e.g. "Imagery/NDVI.png". */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Provenance")
	FString SourcePath;

	/** The `ue_ready[].sha256` the bundle declared for those bytes, verified before import. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Provenance")
	FString Sha256;

	/** The ETL window/job id of the bundle. Changes on every rebuild of the same AOI. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Coverage Raster|Provenance")
	FString JobId;
};
