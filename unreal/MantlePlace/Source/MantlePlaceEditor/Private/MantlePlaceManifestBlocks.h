// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * The per-artifact sub-structs FMantlePlaceVaultManifest is composed of. Each is plain data, filled
 * by the matching handler's ParseFields (MantlePlaceArtifactHandler.h) and read by that handler's
 * importer. A block carries only what the manifest published for it -- every value is applied
 * verbatim and none is derived here (HPS-33). Adding an artifact adds one block to this file.
 */

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

/** `hosts.unreal.georeference`: this host's frame. Read by Parse itself (it is not an artifact); the
 *  origin is the anchor every projected coordinate is taken relative to
 *  (FMantlePlaceVaultManifest::ProjectedToUeCm). */
struct FMantlePlaceGeoreferenceBlock
{
	int32 Epsg = 0;
	FString CrsProjected;             // georeference.crs_projected verbatim ("EPSG:32613"): this host's
	                                  // frame, the one a pointer's stated `crs` is compared against (HPS-53)
	double OriginEastingM = 0.0;
	double OriginNorthingM = 0.0;
	double GroundOrthometricHM = 0.0; // elevation at the centroid (= mesh ground z=0 reference)
	FString PlanetShape;              // contract: "Flat" only; Parse rejects any other value
};

/** The bundle's own Cesium-ready artifacts (v8) and AOI bbox: top-level siblings of `unreal`, so
 *  present even when the `unreal` block is not. Streaming only; no handler imports from it.
 *  NB: `layout.imagery` (the Imagery.pmtiles pointer) is deliberately NOT carried: Cesium for Unreal
 *  has no PMTiles raster overlay, and a parsed field with no reader advertises support this plugin
 *  does not have. */
struct FMantlePlaceCesiumBlock
{
	bool bHasTerrain = false;
	FString TerrainPath;            // layout.cesium_terrain, e.g. "Terrain/layer.json" (quantized-mesh root)
	int32 TerrainTileCount = 0;     // cesium_terrain.tile_count
	bool bHasBbox = false;          // top-level AOI bbox in WGS84 degrees (for the raster-overlay rect)
	double BboxWestDeg = 0.0;
	double BboxSouthDeg = 0.0;
	double BboxEastDeg = 0.0;
	double BboxNorthDeg = 0.0;
};

/** `unreal.heightmap` (-> Landscape). */
struct FMantlePlaceHeightmapBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Elevation/Heightmap.png"
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
	FString Sha256;                 // sha256 of the raw heightmap PNG bytes; schema-required

	/** Quads per component = SectionsPerComponent * SectionSizeQuads. */
	int32 GetQuadsPerComponent() const { return SectionsPerComponent * SectionSizeQuads; }
};

/** `unreal.imagery_drape` (-> Texture2D + drape material). */
struct FMantlePlaceDrapeBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Imagery/Imagery.png"
	double LeftM = 0.0;             // UTM extent (same CRS as the georeference origin)
	double BottomM = 0.0;
	double RightM = 0.0;
	double TopM = 0.0;
	FString Sha256;                 // sha256 of the raw imagery PNG bytes; schema-required
	FString Alignment;              // `alignment` verbatim: the ETL's truthful drape-vs-heightmap extent
	                                // descriptor. A FREE STRING by contract, matched on its prefix and
	                                // never on the whole value -- the divergent shape embeds measured
	                                // numbers. Classified by FMantlePlaceDrapeAlignmentLogic; a "matches"
	                                // claim the extents refute is refused by Parse.
};

/** `unreal.mesh_alternative` (-> static mesh). */
struct FMantlePlaceMeshBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Mesh/Terrain.glb"
	FString UpAxis;                 // contract: "y" only (Interchange converts Y-up -> Z-up
	                                // unconditionally); Parse rejects any other declared value
	bool bNaniteRecommended = false;
	FString Sha256;                 // schema keeps it optional, so empty = check skipped
	FString AbsentReason;           // hosts.unreal.readiness.mesh_import.reason when the ETL produced no mesh
	                                // (e.g. "mesh_not_produced"); empty when a mesh is present
};

/** `unreal.foliage_points` (-> DataTable scatter input; HPS-32). This host's own pointer: absent
 *  means the bundle has no tree points -- never fall back to `layout.tree_points` or
 *  `landcover.tree_points` (HPS-33). */
struct FMantlePlaceFoliagePointsBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Landcover/TreePoints.csv"
	FString Sha256;                 // OPTIONAL in the schema; empty means "valid but unverified", never corrupt
	int32 Count = 0;                // point_count; 0 when omitted. The parsed row count is checked against
	                                // it -- a digest proves the bytes, and only the count proves the ROWS.
	FString Crs;                    // crs, verbatim; empty when not stated.
	FString Units;                  // units (ground_z), verbatim.
	FString HorizontalUnits;        // horizontal_units (x, y), verbatim. All three are REQUIRED from
	                                // MPB 1.3.0; FMantlePlaceTreePointsLogic decides what they mean.
	FString FoliageTypeVocabulary;  // landcover.tree_points.foliage_type_vocabulary, verbatim; empty when the
	                                // manifest names none (MPB 1.2.0). It describes the VALUES of the CSV's
	                                // foliage_type column, which the shared tree-points block is the only
	                                // place to publish: it is read for that and for nothing else, and the
	                                // path and digest stay this block's own (HPS-33).
};

/** `unreal.landscape_layers`: Landscape-paintable rasters this host is addressed by name. All are
 *  parsed; `material_weights` is applied by the landscape, the rest become data textures. Never part
 *  of bValid. */
struct FMantlePlaceLandscapeLayersBlock
{
	bool bPresent = false;
	TArray<FMantlePlaceLandscapeLayer> Layers; // sorted by Name, so the order is stable
};

/** `unreal.buildings_mesh` (-> static mesh; ALL/"Unreal" scope), sharing the terrain's Local Projected Frame. */
struct FMantlePlaceBuildingsBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Mesh/Buildings.glb"
	FString UpAxis;                 // "y" only, same fail-closed rule as the terrain mesh
	FString Sha256;                 // from the top-level `buildings.formats[]` entry matching the path;
	                                // empty if the manifest omits it (integrity check then skipped)
};

/** The derived "road_splines" layer of the top-level `vector.layers[]` (-> spline actors). The importer
 *  consumes the GeoJSON format only; a gpkg-only bundle (base tier) ships none. */
struct FMantlePlaceRoadSplinesBlock
{
	bool bPresent = false;
	FString Path;                   // in-zip path, e.g. "Vector/RoadSplines.geojson"
	FString Sha256;                 // from the matching vector.layers[].formats[] entry; empty = skip check
	FString VerticalDatum;          // vector.layers[road_splines].vertical_datum, verbatim (MPB 1.8.0);
	                                // FMantlePlaceRoadSplinesLogic::CanPlaceHeights decides
};
