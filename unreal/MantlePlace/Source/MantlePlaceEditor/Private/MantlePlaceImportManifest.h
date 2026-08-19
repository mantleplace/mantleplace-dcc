// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

class FJsonObject;

/**
 * How to read a UE-ready raster's pixels back as data (`ue_ready[].value_mapping`). The shape
 * follows the raster's `encoding`, so only the fields that encoding uses are ever populated:
 *   png-16bit-grayscale -> MinValue/MaxValue/ToValueFormula, optional Units and NodataValue
 *   png-8bit-mask       -> TrueValue/FalseValue
 *   png-8bit-indexed    -> Classes (the class codes ARE the legend and are never rescaled)
 *   png-8bit-rgba       -> Bands, naming the four channels in order
 *   png-8bit-grayscale  -> bIdentity (an already-8-bit raster copied through verbatim)
 * Without this a 16-bit grayscale PNG is an arbitrary 0-65535 ramp rather than metres or degrees,
 * which is why the contract makes it required rather than decorative.
 */
struct FMantlePlaceRasterValueMapping
{
	double MinValue = 0.0;
	double MaxValue = 0.0;
	FString ToValueFormula;   // e.g. "value = min + (u16/65535)*(max - min)"
	FString Units;            // e.g. "m", "degrees"; empty when the layer is unitless
	bool bHasNodata = false;
	double NodataValue = 0.0;
	double TrueValue = 0.0;
	double FalseValue = 0.0;
	TArray<int32> Classes;    // e.g. the ESA WorldCover codes 10/20/30/50/60/80
	TArray<FString> Bands;    // e.g. { water, grass, forest, dirt }
	bool bIdentity = false;
};

/** One UE-ready PNG companion of a landscape layer. UE imports textures, not Float32 GeoTIFFs, so
 *  the pipeline pre-converts and this plugin stays a dumb consumer of the result. */
struct FMantlePlaceUeReadyRaster
{
	FString Path;      // in-zip path, e.g. "Landcover/MaterialWeights_1_4.png"
	FString Sha256;    // verified fail-closed before any actor spawns
	FString Encoding;  // one of the png-* encodings the value mapping above is keyed by
	int32 Width = 0;
	int32 Height = 0;
	int64 SizeBytes = 0;
	FMantlePlaceRasterValueMapping ValueMapping;
};

/** One entry of `unreal.landscape_layers`: a Landscape-paintable raster, its hash, its band legend
 *  (material_weights only) and its UE-ready PNG companions. */
struct FMantlePlaceLandscapeLayer
{
	FString Name;               // the block's own key, e.g. "material_weights", "water_mask"
	FString Path;               // the source GeoTIFF's in-zip path
	FString Sha256;
	TArray<FString> Materials;  // frozen band legend, material_weights only; empty elsewhere
	TArray<FMantlePlaceUeReadyRaster> UeReady;
};

/**
 * The pre-baked, UE-ready subset of a vault bundle's Metadata/manifest.json — i.e. the
 * `unreal` block (the platform<->host contract). Pure data + placement math, no engine actors,
 * so it is unit-testable headless. The platform computes every transform value; we apply
 * them verbatim and never re-derive them.
 *
 * Coordinate conventions (validated against the contract):
 *  - The georeference origin (AOI centroid, UTM easting/northing) maps to UE world (0,0).
 *  - East -> +X, North -> +Y, Up -> +Z; 1 uu = 1 cm.
 *  - The *_percent transform values are the Landscape actor DrawScale3D *directly* (the UE
 *    convention where a scale of 100 == the default 1 m / quad and 512 m full uint16 range).
 */
struct FMantlePlaceVaultManifest
{
	bool bValid = false;
	FString JobId;                  // ETL window/job id (changes on each rebuild) — NOT the order id
	FString OrderId;                // vault order id (orders.id); present on order-linked cloud bundles,
	                                 // empty on legacy / local / admin ones. The join key used to
	                                 // materialize a locally-imported bundle that lacks Unreal formats.
	int32 Version = 0;              // top-level manifest schema version (7, 8, …); 0 if absent
	FString DeliveryModel;          // packaging.delivery_model, e.g. "base_on_demand" (v14 Vault
	                                 // Pick-and-Process); empty if absent

	// --- Heightmap (-> Landscape) ------------------------------------------------------
	bool bHasHeightmap = false;
	FString HeightmapPath;          // in-zip path, e.g. "Elevation/Heightmap.png"
	int32 Resolution = 0;           // vertices per edge (e.g. 505)
	int32 SectionSizeQuads = 0;     // e.g. 63
	int32 SectionsPerComponent = 0; // e.g. 2
	int32 ComponentCountX = 0;      // e.g. 4
	int32 ComponentCountY = 0;      // e.g. 4
	double ScaleXPercent = 0.0;     // DrawScale3D.X (cm per quad)
	double ScaleYPercent = 0.0;     // DrawScale3D.Y
	double ScaleZPercent = 0.0;     // DrawScale3D.Z (100 == 512 m over full uint16)
	double LocationZOffsetCm = 0.0; // actor Z so mid-height (uint16 32768) sits at mean elevation
	double ZMinM = 0.0;
	double ZMaxM = 0.0;
	bool bRow0IsNorth = true;       // PNG row 0 is the northern edge
	FString HeightmapSha256;        // sha256 of the raw heightmap PNG bytes; empty if the manifest omits it

	// --- Georeference (flat planet, single UTM zone) -----------------------------------
	int32 Epsg = 0;
	double OriginEastingM = 0.0;
	double OriginNorthingM = 0.0;
	double GroundOrthometricHM = 0.0; // elevation at the centroid (= mesh ground z=0 reference)
	FString PlanetShape;            // contract: "Flat" only; Parse rejects any other value

	// --- Imagery drape (-> Texture2D + drape material) ---------------------------------
	bool bHasDrape = false;
	FString DrapePath;              // in-zip path, e.g. "Imagery/Imagery.png"
	double DrapeLeftM = 0.0;        // UTM extent (same CRS as the georeference origin)
	double DrapeBottomM = 0.0;
	double DrapeRightM = 0.0;
	double DrapeTopM = 0.0;
	FString DrapeSha256;            // sha256 of the raw imagery PNG bytes; empty if the manifest omits it

	// --- Mesh alternative (-> static mesh) ---------------------------------------------
	bool bHasMesh = false;
	FString MeshPath;               // in-zip path, e.g. "Mesh/Terrain.glb"
	FString MeshUpAxis;             // contract: "y" only (Interchange converts Y-up -> Z-up
	                                 // unconditionally); Parse rejects any other declared value
	bool bNaniteRecommended = false;
	FString MeshSha256;             // sha256 of the glb bytes (v17 emits it; schema keeps it optional,
	                                // so empty = check skipped)
	FString MeshAbsentReason;       // dcc_readiness.unreal.mesh_import.reason when the ETL produced no mesh
	                                // (e.g. "mesh_not_produced"); empty when a mesh is present

	// --- Foliage points (-> DataTable scatter input; HPS-32) ---------------------------
	// This host's own tree-points pointer. Absent means the bundle simply has no tree points, not
	// an error — do not fall back to `layout.tree_points` or `landcover.tree_points` (HPS-33).
	bool bHasFoliagePoints = false;
	FString FoliagePointsPath; // in-zip path, e.g. "Landcover/TreePoints.csv" (unreal.foliage_points.path)

	// --- Landscape layers (-> Landscape weightmap layers; unreal.landscape_layers) ------
	// Landscape-paintable rasters this host is addressed by name. All of them are parsed and
	// exposed; only `material_weights` is applied today (it is the one that changes what the
	// Landscape IS — the other seven need a material that consumes them, which is product design,
	// not contract repair). Absent when the ETL shipped none; never part of bValid, since a bundle
	// with a heightmap and no landcover is a perfectly good import.
	bool bHasLandscapeLayers = false;
	TArray<FMantlePlaceLandscapeLayer> LandscapeLayers; // sorted by Name, so the order is stable

	// --- Buildings mesh (-> static mesh; ALL/"Unreal" scope) --------------
	// Extruded building massing that shares the terrain's Local Projected Frame (centroid ground at
	// z=0), so it imports with the same GetMeshLocation() transform as the terrain mesh. Present only
	// when the ETL emitted Mesh/Buildings.glb; absent for base bundles and building-less AOIs.
	bool bHasBuildings = false;
	FString BuildingsPath;          // in-zip path, e.g. "Mesh/Buildings.glb" (unreal.buildings_mesh.path)
	FString BuildingsUpAxis;        // "y" only, same fail-closed rule as MeshUpAxis
	FString BuildingsSha256;        // sha256 from the top-level `buildings.formats[]` entry matching the
	                                 // path; empty if the manifest omits it (integrity check then skipped)

	// --- Road splines (-> spline actors; Wave-2 pipeline layers) ------------------------
	// Z-draped road centerlines shipped INSIDE the ODbL Vector/ set as the derived
	// "road_splines" layer. The manifest's top-level `vector.layers[]` carries the per-format
	// entries; the importer consumes the GeoJSON one (WGS84 lon/lat + orthometric Z, plus
	// width_m_estimated / class / name attributes). Absent on base bundles (gpkg-only) and
	// road-less AOIs.
	bool bHasRoadSplines = false;
	FString RoadSplinesPath;   // in-zip path, e.g. "Vector/RoadSplines.geojson"
	FString RoadSplinesSha256; // from the matching vector.layers[].formats[] entry; empty = skip check

	// --- Native Cesium streaming (v8: the bundle's own Cesium-ready artifacts) ----------
	// These let Cesium for Unreal stream the bundle from a local server, alongside the native-asset
	// import. Top-level siblings of `unreal` (so present even when the `unreal` block is too).
	bool bHasCesiumTerrain = false;
	FString CesiumTerrainPath;       // layout.cesiumTerrain, e.g. "Terrain/layer.json" (quantized-mesh root)
	int32 CesiumTerrainTileCount = 0; // cesiumTerrain.tileCount
	// NB: `layout.imagery` (the Imagery.pmtiles pointer) is deliberately NOT carried here. Cesium
	// for Unreal has no PMTiles raster overlay, so nothing could consume it without a tile-server
	// shim that does not exist; the streaming path advertises the drape PNG instead. A parsed field
	// with no reader advertises support this plugin does not have.
	bool bHasBbox = false;           // top-level AOI bbox in WGS84 degrees (for the raster-overlay rect)
	double BboxWestDeg = 0.0;
	double BboxSouthDeg = 0.0;
	double BboxEastDeg = 0.0;
	double BboxNorthDeg = 0.0;

	// --- Placement helpers (apply the contract verbatim) -------------------------------

	/** Landscape actor DrawScale3D = the manifest's *_percent values directly. */
	FVector GetLandscapeScale() const;

	/** Spawn location for the Landscape actor (corner), centring the AOI on world (0,0). */
	FVector GetLandscapeSpawnLocation() const;

	/** Static-mesh actor location: centroid at world (0,0), ground at true orthometric Z. */
	FVector GetMeshLocation() const;

	/** Drape footprint in UE world cm (East/+X, North/+Y). Used by the importer's imagery-coverage
	 *  warning (and to derive the UV transform below). */
	void GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const;

	/**
	 * Grid-relative UV transform for the geometry-local drape material. The material samples the imagery
	 * from a normalised [0,1] coordinate that spans the AOI grid (LandscapeLayerCoords; U->+X/East,
	 * V->+Y/North), then applies `OutScale`/`OutOffset` to land on the imagery's geographic footprint:
	 *   imageryUV = gridUV * OutScale + OutOffset.
	 * Derived so the result equals the legacy world-position projection, but it now rides the surface
	 * through sculpts/moves/Y-mirror (LandscapeLayerCoords is topological, not world-space). For v8
	 * (imagery == AOI) this is the identity: scale (1,1), offset (0,0).
	 */
	void GetDrapeUvTransform(FVector2D& OutScale, FVector2D& OutOffset) const;

	/** Quads per component = SectionsPerComponent * SectionSizeQuads. */
	int32 GetQuadsPerComponent() const { return SectionsPerComponent * SectionSizeQuads; }

	/** The named `unreal.landscape_layers` entry, or nullptr when this bundle ships none. */
	const FMantlePlaceLandscapeLayer* FindLandscapeLayer(const TCHAR* Name) const;
};

namespace MantlePlaceImportManifest
{
/**
	 * Parse a bundle manifest's JSON text into FMantlePlaceVaultManifest. Rejects any manifest
	 * below MantlePlaceMinSupportedManifestVersion fail-closed (clean break: pre-v18 bundles are
	 * re-procured, never dual-parsed), then reads the `unreal` block; sets bValid=false and fills
	 * OutError if the block is missing/malformed (e.g. an unmaterialized base bundle). Never throws.
	 */
FMantlePlaceVaultManifest Parse(const FString& JsonText, FString& OutError);

/** Parse the integer EPSG code out of a "EPSG:32613" string. Returns 0 if not parseable. */
int32 ParseEpsg(const FString& CrsString);

/** True iff the bundle has been materialized for ANY host, decided from the manifest's neutral
	 *  signals (HPS-47): a known host block (`unreal`, `revit`) present as a top-level object, a
	 *  `dcc_readiness` object, or a non-empty `vector.layers` array — never from this host's own
	 *  content. Materialized-for-someone-else is still materialized: answering false there misreads
	 *  a paid-for bundle as base and tells the user to materialize what already exists. Pure and
	 *  headless-testable; driven by the corpus vector case manifest.materializationSignals. */
bool IsBundleMaterialized(const TSharedPtr<FJsonObject>& Root);

/** Zip-entry prefix for streaming this bundle's Cesium terrain: the parent directory of
	 *  CesiumTerrainPath plus a trailing slash (e.g. "CesiumTerrain/layer.json" -> "CesiumTerrain/",
	 *  "Terrain/layer.json" -> "Terrain/"). Empty if CesiumTerrainPath is empty or has no directory
	 *  component. */
FString DeriveCesiumTerrainPrefix(const FString& CesiumTerrainPath);
}
