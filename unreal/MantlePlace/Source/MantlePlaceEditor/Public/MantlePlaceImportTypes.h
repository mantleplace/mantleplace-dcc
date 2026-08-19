// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceImportTypes.generated.h"

/** Which representation(s) to build from a vault package. */
UENUM(BlueprintType)
enum class EMantlePlaceImportMode : uint8
{
	/** Heightmap -> native Landscape (the headline path). */
	Landscape,
	/** Terrain.glb -> static mesh. */
	Mesh,
	/** Build both a Landscape and a Mesh (they overlay; same imagery drape). */
	Both
};

/** Outcome of an import, surfaced to the vault panel's status log. */
USTRUCT(BlueprintType)
struct FMantlePlaceImportResult
{
	GENERATED_BODY()

	/** True only if every requested representation was created. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	bool bSuccess = false;

	/** Human-readable, multi-line summary for the vault panel (success details or the failure reason). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString Message;

	/** Labels of the actors that were spawned into the level. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	TArray<FString> CreatedActors;

	/** The bundle's jobId (from the manifest), for reference. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString JobId;
};

/**
 * Result of starting a local Cesium stream for a bundle. The local loopback server hosts the bundle's
 * own Cesium-ready quantized-mesh terrain + imagery; these URLs + the AOI bbox are what the Python
 * wiring uses to spawn an ACesium3DTileset and a raster overlay under the level's CesiumGeoreference.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceStreamInfo
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	bool bSuccess = false;

	/** Human-readable status (success details or the failure reason). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString Message;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString JobId;

	/** Base URL of the local server, e.g. "http://127.0.0.1:8088". */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString BaseUrl;

	/** Full URL of the quantized-mesh terrain layer.json (set the Cesium3DTileset's Url to this). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString CesiumTerrainUrl;

	/** Full URL of the AOI imagery PNG (single-tile raster overlay source); empty if no drape imagery. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	FString ImageryUrl;

	/** AOI bounding box in WGS84 degrees, for the raster overlay rectangle. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	bool bHasBbox = false;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	double BboxWestDeg = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	double BboxSouthDeg = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	double BboxEastDeg = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Import")
	double BboxNorthDeg = 0.0;
};
