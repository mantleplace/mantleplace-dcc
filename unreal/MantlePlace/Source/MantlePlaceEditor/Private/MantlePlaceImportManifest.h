// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceManifestBlocks.h"

class FJsonObject;
struct FMantlePlaceRoadSplinesDatum;
struct FMantlePlaceTreePointsFrame;

/**
 * The manifest's top-level `delivery` block: which unit system the order asked for, which linear unit
 * its delivered files are in, and which grid they are stated on. Host-neutral, and read here for ONE
 * purpose -- telling the user what the bundle is in (HPS-51's words, FMantlePlaceDeliveryLogic).
 *
 * No placement value comes from it. This host is fixed-frame (HPS-54): its origin and every file its
 * own block points at are metric UTM on every order, so `hosts.unreal` stays the only source of
 * anything placed (HPS-33, HPS-52). That is also why the values are kept as the raw published strings
 * rather than enums, and why an unknown one is not a refusal: nothing here acts on them, so there is
 * nothing to fail closed (HPS-35) -- a value this host has no word for is shown as published.
 *
 * The CRS is named by `delivery.label` where the manifest carries one (MPB 1.3.0 onwards), verbatim.
 * An earlier bundle has none, and the line prints the EPSG code instead: the schema says the same words
 * follow from `tier` and `horizontal_epsg`, but reaching them is a CRS-name lookup, and this plugin
 * keeps no table of CRS names.
 */
struct FMantlePlaceDeliveryFacts
{
	bool bDeclared = false;          // the manifest carried a `delivery` object at all; bundles built
	                                  // before the block existed carry none, and say nothing
	FString UnitSystem;              // delivery.unit_system verbatim ("metric", "imperial"); empty if absent
	FString LinearUnit;              // delivery.linear_unit verbatim ("m", "ftUS", "ft"); empty if absent
	bool bHasHorizontalEpsg = false; // false when delivery.horizontal_epsg is absent or null -- null is
	                                  // what the `local_ft` tier publishes, whose files have no named CRS
	int32 HorizontalEpsg = 0;        // delivery.horizontal_epsg, e.g. 6543
	FString Label;                   // delivery.label verbatim, e.g. "WGS 84 / UTM zone 13N"; empty on
	                                  // bundles built before 1.3.0 published it
};

/**
 * The pre-baked, UE-ready subset of a vault bundle's Metadata/manifest.json — i.e. the
 * `unreal` block (the platform<->host contract). Pure data + placement math, no engine actors,
 * so it is unit-testable headless. The platform computes every transform value; we apply
 * them verbatim and never re-derive them.
 *
 * Coordinate conventions (validated against the contract):
 *  - The georeference origin (AOI centroid, UTM easting/northing) maps to UE world (0,0).
 *  - **North -> +X, East -> +Y, Up -> +Z**; 1 uu = 1 cm. This is Unreal's LEFT-handed world
 *    frame, and the swap versus the manifest's own grid-axis naming is deliberate: the ETL
 *    calls its easting axis "x" (`scale_x_percent`, `component_count_x`), so those values are
 *    applied to UE **Y**. Mapping East -> +X instead swaps two axes, which is a reflection
 *    (determinant -1) rather than a rotation, and silently mirrors every imported bundle.
 *    Route every projected coordinate through ProjectedToUeCm() rather than open-coding it.
 *  - The *_percent transform values are the Landscape actor DrawScale3D *directly* (the UE
 *    convention where a scale of 100 == the default 1 m / quad and 512 m full uint16 range),
 *    after the axis swap above.
 */
struct FMantlePlaceVaultManifest
{
	bool bValid = false;
	FString JobId;                  // ETL window/job id (changes on each rebuild) — NOT the order id
	FString OrderId;                // vault order id (orders.id); present on order-linked cloud bundles,
	                                 // empty on legacy / local / admin ones. The join key used to
	                                 // materialize a locally-imported bundle that lacks Unreal formats.
	FString Version;                // top-level manifest version, verbatim ("1.0.0"); empty if absent.
	                                 // A STRING in the MPB era; kept unparsed so a refusal can quote
	                                 // exactly what the bundle said, including an integer-era value
	                                 // this reader does not speak.
	FString DeliveryModel;          // packaging.delivery_model, e.g. "base_on_demand" (v14 Vault
	                                 // Pick-and-Process); empty if absent
	FMantlePlaceDeliveryFacts Delivery; // the top-level `delivery` block, for DISPLAY only -- see the type

	// --- Core blocks: not artifacts, read by Parse itself -------------------------------
	FMantlePlaceGeoreferenceBlock Georef;
	FMantlePlaceCesiumBlock Cesium;

	// `elevation.dem.bounds_target_crs` is DECLINED by this host, not overlooked — see
	// docs/adr/0006-unreal-declines-elevation-bounds-target-crs.md. It is the delivered DEM grid's
	// bounds in the DELIVERY frame, host-neutral and addressed to nobody in particular, while this
	// host is already given every ground extent it applies inside `hosts.unreal`: the drape's in
	// `imagery_drape.extent` and the heightmap's in `heightmap.landscape_transform`. Reading it
	// would give placement two sources of truth and this host no authority to reconcile them
	// (HPS-33). There is deliberately no field for it: a parsed field with no reader is exactly the
	// "published and ignored" state that record exists to close.

	// --- One sub-struct per artifact: each is parsed by its own handler ------------------
	// (MantlePlaceArtifactHandler.h). A new artifact adds its block here and nothing else in this
	// struct; Parse walks the handler registry, so it does not change.
	FMantlePlaceHeightmapBlock Heightmap;             // -> Landscape
	FMantlePlaceDrapeBlock Drape;                     // -> Texture2D + drape material
	FMantlePlaceMeshBlock Mesh;                       // -> static mesh
	FMantlePlaceFoliagePointsBlock Foliage;           // -> DataTable scatter input (HPS-32)
	FMantlePlaceLandscapeLayersBlock LandscapeLayers; // -> coverage-raster data textures
	FMantlePlaceBuildingsBlock Buildings;             // -> static mesh
	FMantlePlaceRoadSplinesBlock RoadSplines;         // -> spline actors

	// --- Placement helpers (apply the contract verbatim) -------------------------------

	/**
	 * Projected metres, relative to the georeference origin, into UE world cm. The ONE place the
	 * projected->UE axis mapping lives: North -> +X, East -> +Y, Up -> +Z.
	 */
	static FVector ProjectedToUeCm(double DeltaEastingM, double DeltaNorthingM, double UpM);

	/** The AOI's full span in UE world cm, in UE axis order (X = North, Y = East). The manifest's
	 *  grid axes are named the other way round, so this is where that swap is applied once. */
	FVector2D GetAoiSizeUeCm() const;

	/**
	 * Actor rotation for the bundle's glTF-sourced actors (terrain `mesh_alternative`, `buildings_mesh`).
	 *
	 * Interchange lands these meshes with **East on +X and South on +Y** — measured, not assumed:
	 * the terrain mesh's >100 m vertices centroid at local (-572.3, +228.9) m against a heightmap
	 * whose true high-ground centroid is 572.8 m west and 219.2 m south of the AOI centre. The
	 * world frame is North -> +X, East -> +Y (ProjectedToUeCm), so mapping mesh(East, South) onto
	 * world(North, East) is (x, y) -> (-y, x): a **+90 degree yaw, determinant +1**. It is a pure
	 * rotation, so the meshes need no mirroring and no negative scale.
	 *
	 * This is why the mesh path differs from the raster path, which needed a transpose: the glTF
	 * arrives right-handed and Interchange's Y-up->Z-up conversion already performs the handedness
	 * flip, leaving only an in-plane rotation. The heightmap is a bare raster with no
	 * such conversion, so its correction is a reflection instead.
	 *
	 * Historical note: against the OLD, mirrored landscape (East -> +X, North -> +Y) these meshes
	 * differed by exactly a Y negation, which is why a `-1` Y scale appeared to line them up. That
	 * workaround mirrored the geometry to match a mirrored terrain; this rotation does not.
	 */
	static FRotator GetMeshRotation();

	/** Landscape actor DrawScale3D = the manifest's *_percent values, swapped into UE axis order. */
	FVector GetLandscapeScale() const;

	/** Spawn location for the Landscape actor (corner), centring the AOI on world (0,0). */
	FVector GetLandscapeSpawnLocation() const;

	/** Static-mesh actor location: centroid at world (0,0), ground at true orthometric Z. */
	FVector GetMeshLocation() const;

	/** Drape footprint in UE world cm (North/+X, East/+Y). Used by the importer's imagery-coverage
	 *  warning (and to derive the UV transform below). */
	void GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const;

	/**
	 * Grid-relative UV transform for the geometry-local drape material. The material samples the imagery
	 * from a normalised [0,1] coordinate that spans the AOI grid (LandscapeLayerCoords; U->+X/North,
	 * V->+Y/East), then applies `OutScale`/`OutOffset` to land on the imagery's geographic footprint:
	 *   t         = gridUV * OutScale + OutOffset   // still (North, East); 0 at the drape's S/W edge
	 *   imageryUV = (t.y, 1 - t.x)                  // (East, 1 - North)
	 * That second line is load-bearing and lives in M_MantlePlace_Drape, because scale-and-offset can
	 * express neither half of it:
	 *   - the swizzle, because these values are in landscape-grid order (X=North) while the imagery
	 *     PNG is north-up, so its own U is East;
	 *   - the `1 -`, because t.x grows northward from the drape's south edge whereas texture V grows
	 *     downward from its north edge. It is applied AFTER scale/offset deliberately: those place the
	 *     grid within the drape rect, and only then does the flip turn a north-position into a texture
	 *     coordinate. Folding the flip in earlier would be wrong by `1 - OutScale - 2*OutOffset`.
	 * Derived so the result equals the legacy world-position projection, but it now rides the surface
	 * through sculpts/moves/Y-mirror (LandscapeLayerCoords is topological, not world-space). For v8
	 * (imagery == AOI) this is the identity: scale (1,1), offset (0,0).
	 */
	void GetDrapeUvTransform(FVector2D& OutScale, FVector2D& OutOffset) const;

	/** Quads per component = SectionsPerComponent * SectionSizeQuads. */
	int32 GetQuadsPerComponent() const { return Heightmap.GetQuadsPerComponent(); }

	/** The named `unreal.landscape_layers` entry, or nullptr when this bundle ships none. */
	const FMantlePlaceLandscapeLayer* FindLandscapeLayer(const TCHAR* Name) const;

	/**
	 * The frame `unreal.foliage_points` states, beside this host's own, for
	 * FMantlePlaceTreePointsLogic::ParseCsv. Marked required when the manifest's version is one
	 * whose format obliges the pointer to state it, so a missing frame there is refused rather than
	 * mistaken for a bundle built before the format had one.
	 */
	FMantlePlaceTreePointsFrame GetFoliagePointsFrame() const; // callers include MantlePlaceTreePointsLogic.h

	/**
	 * The vertical datum the road layer states, for FMantlePlaceRoadSplinesLogic::CanPlaceHeights.
	 * Marked required when the manifest's version is one whose format states the datum of a
	 * layer's heights, so a missing datum there is refused rather than mistaken for a bundle built
	 * before the format had one.
	 */
	FMantlePlaceRoadSplinesDatum GetRoadSplinesDatum() const; // callers include MantlePlaceRoadSplinesLogic.h
};

namespace MantlePlaceImportManifest
{
/**
	 * Parse a bundle manifest's JSON text into FMantlePlaceVaultManifest. Rejects any manifest
	 * below MantlePlaceMinSupportedManifestVersion fail-closed, and any whose MAJOR is above the
	 * supported line (clean break: older bundles are re-procured and never dual-parsed; newer
	 * majors are refused gracefully rather than best-effort parsed). Then reads the `hosts.unreal`
	 * block; sets bValid=false and fills OutError if the block is missing/malformed (e.g. an
	 * unmaterialized base bundle). Never throws.
	 */
FMantlePlaceVaultManifest Parse(const FString& JsonText, FString& OutError);

/** Parse the integer EPSG code out of a "EPSG:32613" string. Returns 0 if not parseable. */
int32 ParseEpsg(const FString& CrsString);

/** True iff the bundle has been materialized for ANY host, decided from the manifest's neutral
	 *  signals (HPS-47): a `hosts` object with at least one key — no host id is ever compared —
	 *  or a non-empty `vector.layers` array; never from this host's own content. At MPB 1.0.0 this
	 *  is ONE signal where the integer era had three (a known host block, a `dcc_readiness`
	 *  object, a host roster). Materialized-for-someone-else is still materialized: answering
	 *  false there misreads a paid-for bundle as base and tells the user to materialize what
	 *  already exists. Pure and headless-testable; driven by the corpus vector case
	 *  manifest.materializationSignals. */
bool IsBundleMaterialized(const TSharedPtr<FJsonObject>& Root);

/** Zip-entry prefix for streaming this bundle's Cesium terrain: the parent directory of
	 *  CesiumTerrainPath plus a trailing slash (e.g. "CesiumTerrain/layer.json" -> "CesiumTerrain/",
	 *  "Terrain/layer.json" -> "Terrain/"). Empty if CesiumTerrainPath is empty or has no directory
	 *  component. */
FString DeriveCesiumTerrainPrefix(const FString& CesiumTerrainPath);
}
