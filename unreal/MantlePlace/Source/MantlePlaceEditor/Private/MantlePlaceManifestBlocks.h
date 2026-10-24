// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceEditorSettings.h" // EMantlePlaceTreeLevel

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

/** How one published tree-points level is stated (`foliage_points.levels.<LEVEL>`). */
enum class EMantlePlaceTreeLevelForm : uint8
{
	Absent,  // nothing usable stated: for MAX the entry itself; for MED and MIN an unavailable level
	Pointer, // a file of its own (a bundle older than 2.0 ships TreePoints-Med.csv and the like)
	RowCut,  // the first `Rows` data rows of the MAX file (2.0: one ranked file, a row's number is its rank)
	SameAs,  // the same as another level
};

/** One level of `foliage_points.levels`, verbatim. Judged by FMantlePlaceTreeLevelLogic. */
struct FMantlePlaceTreeLevelEntry
{
	EMantlePlaceTreeLevelForm Form = EMantlePlaceTreeLevelForm::Absent;
	FString Path;                   // Pointer: the level's own file
	FString Sha256;                 // Pointer: its digest; empty = unverified
	int32 Count = 0;                // Pointer: its point_count; 0 = unstated
	FString Crs;                    // Pointer: the file's own frame, as stated; all three empty = the block's
	FString Units;
	FString HorizontalUnits;
	int32 Rows = 0;                 // RowCut: the row count
	FString SameAs;                 // SameAs: the named level, verbatim ("MAX", "MED", ...)
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
	FMantlePlaceTreeLevelEntry LevelMax; // `levels` (MPB 1.9.0), by level; RAW is never imported
	FMantlePlaceTreeLevelEntry LevelMed;
	FMantlePlaceTreeLevelEntry LevelMin;
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
 *  reads the layer's GeoPackage when its `gpkg` format row is there, and falls back to the GeoJSON
 *  row when it is not: the two rows are the same roads in the same frame. */
struct FMantlePlaceRoadSplinesBlock
{
	bool bPresent = false;          // the GeoJSON row is declared (the fallback source)
	FString Path;                   // in-zip path, e.g. "Vector/RoadSplines.geojson"
	FString Sha256;                 // from the matching vector.layers[].formats[] entry; empty = skip check

	bool bGpkgPresent = false;      // the GeoPackage row is declared (the source read first)
	FString GpkgPath;               // in-zip path, e.g. "Vector/RoadSplines.gpkg"
	FString GpkgSha256;             // from the gpkg format row; empty = skip check
	TArray<FString> GpkgTables;     // the row's `tables` member; empty = every feature table in the file
	FString VerticalDatum;          // vector.layers[road_splines].vertical_datum, verbatim (MPB 1.8.0);
	                                // FMantlePlaceRoadSplinesLogic::CanPlaceHeights decides
};

/**
 * The public-record layers a bundle carries (parcels, zoning, flood zones and the families that
 * follow them), read from the manifest's top level and not from `hosts.unreal`: the files are the
 * public record passed through unchanged, addressed to every host alike. A layer is one family
 * block (`parcels`, `zoning`, `flood`, ... or a family this reader has not met); each file in it is
 * located by the `path` its own entry states, or by its `layout` key, and never by folder -- a
 * bundle built before the `Records/` folder keeps `Parcels/`, `Zoning/` and `Flood/` at its root
 * and is read by the same code. Everything here is the manifest's, verbatim (HPS-33).
 */

/**
 * The frame a record file states for itself: the same object as `hosts.unreal.file_frame`. Read when
 * a file's entry carries one; no published manifest version declares it for these files yet, so
 * today it is usually absent, and a file that states none cannot be shown to be in this host's frame
 * (HPS-53). FMantlePlacePublicRecordsLogic::CanPlaceFrame decides what a stated one means.
 */
struct FMantlePlaceRecordFrame
{
	bool bStated = false;           // the entry carried a `file_frame` object at all
	FString Type;                   // "projected" | "local", verbatim
	FString Crs;                    // verbatim, e.g. "EPSG:32613"; empty when not stated
	FString HorizontalUnit;         // verbatim; empty when not stated
	FString VerticalUnit;           // verbatim; empty when not stated
};

/** One file of a record layer: a GeoPackage or DXF linework. */
struct FMantlePlaceRecordFile
{
	FString Kind;                   // the entry's own key under the family block: "gpkg", "dxf", "nfhl"
	FString Path;                   // in-zip path, from the entry's `path` or the family's layout key
	FString Sha256;                 // empty = valid but unverified, never corrupt
	FString Source;                 // the entry's `source`, verbatim; empty when absent
	FString License;                // the entry's `license`, verbatim; empty when absent
	FMantlePlaceRecordFrame Frame;
	bool IsDxf() const { return Path.EndsWith(TEXT(".dxf"), ESearchCase::IgnoreCase); }
};

/**
 * A record pointer (MPB 1.11.0): who holds a public record the bundle does not deliver for part of its
 * extent, and where the holder publishes it. A pointer to a page, never data.
 */
struct FMantlePlaceRecordPointer
{
	FString RecordHolder;
	FString Jurisdiction;
	FString State;
	FString Link;                   // empty where the holder's page is not known (null in the manifest)
	FString Reason;                 // verbatim: no_open_source | terms_restrict_commercial_use | down_at_build_time
	FString Basis;
	FString SourceId;
};

/** One family of public records: the block's own key, its files, and what the manifest says about it. */
struct FMantlePlaceRecordLayer
{
	FString Family;                 // the family block's key, verbatim: "parcels", "wetlands", a new one
	TArray<FMantlePlaceRecordFile> Files;
	TArray<FMantlePlaceRecordPointer> Pointers;
	// The text the manifest carries for the layer, verbatim and shown as published. Empty = absent.
	// No published schema version declares these keys yet; they are read by these provisional names
	// and the standing notice (FMantlePlacePublicRecordsLogic::StandingNotice) is shown regardless.
	FString Credit;
	FString Disclaimer;
	FString Notice;
};

/** `completeness.skipped_sources[]` (MPB 1.11.0): a record source that covers the extent and delivered nothing. */
struct FMantlePlaceSkippedSource
{
	FString Layer;                  // "parcels" | "zoning"; empty for a layer this reader does not name
	FString SourceId;
	FString RecordHolder;
	FString Reason;                 // verbatim: source_down | source_failed | source_timeout
};

struct FMantlePlacePublicRecordsBlock
{
	bool bPresent = false;          // at least one layer, or at least one source that delivered nothing
	TArray<FMantlePlaceRecordLayer> Layers; // in the order FMantlePlacePublicRecordsLogic::OrderLayers gives
	TArray<FMantlePlaceSkippedSource> SkippedSources;
};
