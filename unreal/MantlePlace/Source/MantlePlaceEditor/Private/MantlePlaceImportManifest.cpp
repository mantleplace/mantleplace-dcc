// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportManifest.h"

#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion (the v18 clean-break floor)

#include "Dom/JsonObject.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

// ── Placement helpers ──────────────────────────────────────────────────────

FVector FMantlePlaceVaultManifest::ProjectedToUeCm(double DeltaEastingM, double DeltaNorthingM, double UpM)
{
	// The single legal home for the projected->UE axis mapping. Unreal's world is
	// LEFT-handed with X forward/North and Y right/East, so a right-handed map frame maps
	// North -> +X, East -> +Y, Up -> +Z. Writing East -> +X instead swaps two axes, which is a
	// reflection (determinant -1), not a rotation -- it mirrors the whole scene across the NE
	// diagonal and reads, on a near-square AOI, as a 90 degree rotation. Every projected
	// coordinate in this plugin goes through here so that mistake cannot be made twice.
	return FVector(DeltaNorthingM * 100.0, DeltaEastingM * 100.0, UpM * 100.0);
}

FVector2D FMantlePlaceVaultManifest::GetAoiSizeUeCm() const
{
	// The manifest names its grid axes the ETL's way: `scale_x_percent` / `component_count_x`
	// describe the EASTING axis and `*_y` the NORTHING axis. UE X is North, so the X component
	// here takes the *_y values and vice versa. The scale is genuinely anisotropic (the corpus
	// ships 141.369 vs 138.988), so a half-done swap survives as a quiet aspect-ratio error
	// rather than an obvious one -- hence one accessor rather than the arithmetic inline.
	return FVector2D(
		ComponentCountY * GetQuadsPerComponent() * ScaleYPercent,   // North span
		ComponentCountX * GetQuadsPerComponent() * ScaleXPercent);  // East span
}

FRotator FMantlePlaceVaultManifest::GetMeshRotation()
{
	// mesh(East, South) -> world(North, East) is (x, y) -> (-y, x), i.e. yaw +90. A rotation, not
	// a mirror — see the header for the measurement this is derived from.
	return FRotator(0.0, 90.0, 0.0);
}

FVector FMantlePlaceVaultManifest::GetLandscapeScale() const
{
	// The *_percent values ARE the DrawScale3D (100 == 1 m/quad, 512 m full uint16 range),
	// swapped into UE axis order: landscape-local X indexes the heightmap's ROWS (northing) and
	// local Y its COLUMNS (easting), per the transpose in MantlePlaceLandscapeImporter.
	return FVector(ScaleYPercent, ScaleXPercent, ScaleZPercent);
}

FVector FMantlePlaceVaultManifest::GetLandscapeSpawnLocation() const
{
	// Mirror the engine's New-Landscape centring: the actor location is the corner; offset it
	// by half the (scaled) quad span so the AOI centre lands on world (0,0). Identity rotation,
	// so TransformVector(scale) is a component-wise multiply.
	const FVector2D AoiSize = GetAoiSizeUeCm();
	return FVector(-AoiSize.X / 2.0, -AoiSize.Y / 2.0, LocationZOffsetCm);
}

FVector FMantlePlaceVaultManifest::GetMeshLocation() const
{
	// The glb is centroid-local with z=0 at centroid ground; place that at true elevation so it
	// overlays the Landscape (which encodes true orthometric Z too). XY centroid -> world (0,0).
	return FVector(0.0, 0.0, GroundOrthometricHM * 100.0);
}

void FMantlePlaceVaultManifest::GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const
{
	// UTM -> UE world cm relative to the georeference origin (North -> +X, East -> +Y).
	const FVector Min = ProjectedToUeCm(DrapeLeftM - OriginEastingM, DrapeBottomM - OriginNorthingM, 0.0);
	const FVector Size = ProjectedToUeCm(DrapeRightM - DrapeLeftM, DrapeTopM - DrapeBottomM, 0.0);
	OutMin = FVector2D(Min.X, Min.Y);
	OutSize = FVector2D(Size.X, Size.Y);
}

void FMantlePlaceVaultManifest::GetDrapeUvTransform(FVector2D& OutScale, FVector2D& OutOffset) const
{
	// The geometry-local drape samples imagery from a grid-normalised [0,1] coordinate (gridUV) that
	// spans the AOI grid, with U->+X/North and V->+Y/East. NOTE the ordering contract with
	// M_MantlePlace_Drape: the material applies these values in landscape-grid order FIRST, then
	// swizzles AND flips into imagery order LAST -- `t = gridUV*DrapeUvScale + DrapeUvOffset;
	// sample(t.y, 1 - t.x)` -- because the imagery PNG is north-up, so its U is East and its V grows
	// southward, while landscape-local U is now North and grows northward. The flip belongs after the
	// scale/offset: those place the grid inside the drape rect, and only then does `1 -` convert a
	// north-position into a texture coordinate.
	// Everything below therefore stays in UE (X=North, Y=East) order. The AOI is centred on world (0,0), so its
	// world rect is AoiMin = (-AoiSize/2), spanning AoiSize; the imagery spans GetDrapeWorldRect(). The
	// legacy world projection sampled imageryUV = (worldXY - DrapeMin)/DrapeSize. Substituting
	// worldXY = AoiMin + gridUV*AoiSize gives imageryUV = gridUV*(AoiSize/DrapeSize) + (AoiMin-DrapeMin)/DrapeSize.
	// Hence scale = AoiSize/DrapeSize, offset = (AoiMin-DrapeMin)/DrapeSize. For v8 (imagery == AOI) this
	// is the identity. Falls back to identity if the drape rect is degenerate.
	OutScale = FVector2D(1.0, 1.0);
	OutOffset = FVector2D(0.0, 0.0);

	FVector2D DrapeMin, DrapeSize;
	GetDrapeWorldRect(DrapeMin, DrapeSize);
	if (DrapeSize.X <= 0.0 || DrapeSize.Y <= 0.0)
	{
		return;
	}

	const FVector2D AoiSize = GetAoiSizeUeCm();
	const FVector2D AoiMin(-AoiSize.X / 2.0, -AoiSize.Y / 2.0); // AOI centred on world (0,0)

	OutScale = FVector2D(AoiSize.X / DrapeSize.X, AoiSize.Y / DrapeSize.Y);
	OutOffset = FVector2D(
		(AoiMin.X - DrapeMin.X) / DrapeSize.X,
		(AoiMin.Y - DrapeMin.Y) / DrapeSize.Y);
}

const FMantlePlaceLandscapeLayer* FMantlePlaceVaultManifest::FindLandscapeLayer(const TCHAR* Name) const
{
	return LandscapeLayers.FindByPredicate(
		[Name](const FMantlePlaceLandscapeLayer& Layer) { return Layer.Name == Name; });
}

// ── Parsing ────────────────────────────────────────────────────────────────

namespace
{
	const TSharedPtr<FJsonObject>* GetObject(const TSharedPtr<FJsonObject>& Parent, const TCHAR* Field)
	{
		if (!Parent.IsValid())
		{
			return nullptr;
		}
		const TSharedPtr<FJsonObject>* Out = nullptr;
		return Parent->TryGetObjectField(Field, Out) ? Out : nullptr;
	}

	double GetNumber(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, double Default = 0.0)
	{
		double Value = Default;
		if (Obj.IsValid())
		{
			Obj->TryGetNumberField(Field, Value);
		}
		return Value;
	}

	int32 GetInt(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, int32 Default = 0)
	{
		return static_cast<int32>(FMath::RoundToDouble(GetNumber(Obj, Field, static_cast<double>(Default))));
	}

	FString GetString(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field)
	{
		FString Value;
		if (Obj.IsValid())
		{
			Obj->TryGetStringField(Field, Value);
		}
		return Value;
	}

	bool GetBool(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, bool Default)
	{
		bool Value = Default;
		if (Obj.IsValid())
		{
			Obj->TryGetBoolField(Field, Value);
		}
		return Value;
	}

	TArray<FString> GetStrings(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field)
	{
		TArray<FString> Values;
		if (Obj.IsValid())
		{
			Obj->TryGetStringArrayField(Field, Values);
		}
		return Values;
	}

	/**
	 * Enforce the one glTF orientation this importer supports, the way planet_shape enforces "Flat".
	 * Interchange converts Y-up to Z-up unconditionally, so a bundle declaring anything else would
	 * import silently wrong — geometry in the world at a plausible-looking transform, rotated 90
	 * degrees, with no error anywhere. Absent (empty) is tolerated: that is a bundle that never
	 * declared an axis, not one declaring a different one. Returns false with OutError filled.
	 */
	bool CheckUpAxis(const TCHAR* Block, const FString& UpAxis, FString& OutError)
	{
		if (UpAxis.IsEmpty() || UpAxis.Equals(TEXT("y"), ESearchCase::IgnoreCase))
		{
			return true;
		}
		OutError = FString::Printf(
			TEXT("Unsupported up_axis \"%s\" on unreal.%s: this importer converts Y-up glTF only "
			     "(Interchange applies that conversion unconditionally, so any other axis would import "
			     "rotated with no error)."),
			*UpAxis, Block);
		return false;
	}

	/** `ue_ready[].value_mapping` — every shape the five encodings use, read into one struct. Absent
	 *  keys simply stay at their defaults: the mapping's shape follows the encoding, so reading by
	 *  key rather than branching on the encoding means a new encoding is inert rather than lossy. */
	FMantlePlaceRasterValueMapping ParseValueMapping(const TSharedPtr<FJsonObject>& Mapping)
	{
		FMantlePlaceRasterValueMapping Out;
		if (!Mapping.IsValid())
		{
			return Out;
		}
		Out.MinValue = GetNumber(Mapping, TEXT("min"));
		Out.MaxValue = GetNumber(Mapping, TEXT("max"));
		Out.ToValueFormula = GetString(Mapping, TEXT("to_value"));
		Out.Units = GetString(Mapping, TEXT("units"));
		Out.bHasNodata = Mapping->HasField(TEXT("nodata_value"));
		Out.NodataValue = GetNumber(Mapping, TEXT("nodata_value"));
		Out.TrueValue = GetNumber(Mapping, TEXT("true_value"));
		Out.FalseValue = GetNumber(Mapping, TEXT("false_value"));
		Out.bIdentity = GetBool(Mapping, TEXT("identity"), false);
		Out.Bands = GetStrings(Mapping, TEXT("bands"));

		const TArray<TSharedPtr<FJsonValue>>* Classes = nullptr;
		if (Mapping->TryGetArrayField(TEXT("classes"), Classes) && Classes != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& Value : *Classes)
			{
				if (Value.IsValid())
				{
					Out.Classes.Add(static_cast<int32>(FMath::RoundToDouble(Value->AsNumber())));
				}
			}
		}
		return Out;
	}
}

int32 MantlePlaceImportManifest::ParseEpsg(const FString& CrsString)
{
	// Accept "EPSG:32613" (any case) or a bare number.
	FString Digits;
	for (const TCHAR Ch : CrsString)
	{
		if (FChar::IsDigit(Ch))
		{
			Digits.AppendChar(Ch);
		}
	}
	return Digits.IsEmpty() ? 0 : FCString::Atoi(*Digits);
}

FString MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(const FString& CesiumTerrainPath)
{
	const FString Dir = FPaths::GetPath(CesiumTerrainPath);
	return Dir.IsEmpty() ? FString() : (Dir + TEXT("/"));
}

bool MantlePlaceImportManifest::IsBundleMaterialized(const TSharedPtr<FJsonObject>& Root)
{
	if (!Root.IsValid())
	{
		return false;
	}

	// HPS-47: any one of the neutral signals means the ETL has built DCC formats for SOMEBODY.
	//
	// At MPB 1.0.0 this is ONE signal where the integer era had three. Everything host-specific now
	// lives under `hosts.<hostId>` — payloads and readiness alike — so the question is whether that
	// object has ANY key, never which keys it has. No host roster is consulted at all, which is
	// what makes roster staleness structurally unable to matter: a bundle materialized only for a
	// host this plugin has never heard of still answers true.
	//
	// A key, not mere existence: an empty `hosts` object is base-tier scaffolding exactly as an
	// empty `vector.layers` array is.
	if (const TSharedPtr<FJsonObject>* HostsPtr = GetObject(Root, TEXT("hosts")))
	{
		if ((*HostsPtr)->Values.Num() > 0)
		{
			return true;
		}
	}

	// Non-empty layers only: an empty `vector.layers` array is base-tier scaffolding, not a signal.
	if (const TSharedPtr<FJsonObject>* VectorPtr = GetObject(Root, TEXT("vector")))
	{
		const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
		if ((*VectorPtr)->TryGetArrayField(TEXT("layers"), Layers) && Layers != nullptr && Layers->Num() > 0)
		{
			return true;
		}
	}

	return false;
}

FMantlePlaceVaultManifest MantlePlaceImportManifest::Parse(const FString& JsonText, FString& OutError)
{
	FMantlePlaceVaultManifest M;

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonText);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("manifest.json is not valid JSON.");
		return M;
	}

	M.JobId = GetString(Root, TEXT("job_id"));
	// The vault order id (orders.id), written into the manifest for order-linked cloud bundles (it
	// rides through the ETL as a top-level `order_id`; empty on legacy / local-dev / admin
	// bundles). This is the join key that lets a downloaded zip map back to its vault order so an
	// incomplete bundle can be materialized on demand. NB: job_id is the per-rebuild ETL job id and
	// is deliberately NOT used for this - it changes on every rebuild.
	M.OrderId = GetString(Root, TEXT("order_id"));
	M.Version = GetString(Root, TEXT("version"));

	// Clean-break version gate (HPS-31). Read as a STRING and parsed as semver: an MPB version IS
	// a string, so anything that fails to parse — an absent version, an integer from the
	// pre-history, a partial "1.0" — is refused here. Deliberately NOT coerced through a number:
	// the integer era's reader read an absent version as 0 and refused it, and letting a string
	// fall to 0 the same way would be an accident that happens to work rather than a decision.
	const FMantlePlaceManifestVersion Parsed = FMantlePlaceManifestVersion::Parse(M.Version);
	const FMantlePlaceManifestVersion Floor =
	    FMantlePlaceManifestVersion::Parse(MantlePlaceMinSupportedManifestVersion);
	if (!Parsed.bValid || Parsed < Floor)
	{
		OutError = FString::Printf(
		    TEXT("Bundle manifest version %s is no longer supported (minimum %s). Re-download this "
		         "AOI from your vault at mantle.place/vault — rebuilding it there re-cuts the bundle "
		         "on the current pipeline."),
		    M.Version.IsEmpty() ? TEXT("(absent)") : *M.Version, *MantlePlaceMinSupportedManifestVersion);
		return M;
	}

	// The other end of the semver compatibility policy. Minors are strictly additive and unknown
	// fields are ignored, so any 1.x reads here; an unknown higher MAJOR is a graceful refusal
	// rather than a best-effort parse, because a major is exactly the promise that something this
	// reader relies on may have changed meaning. This is a refusal, not a crash, and it names the
	// version it saw so the user can tell "too new" from "too old" — the two failures a bare
	// "unsupported" message makes indistinguishable.
	if (Parsed.Major > Floor.Major)
	{
		OutError = FString::Printf(
		    TEXT("Bundle manifest version %s is newer than this plugin understands (it reads %d.x). "
		         "Update the Mantle Place plugin to import this bundle."),
		    *M.Version, Floor.Major);
		return M;
	}

	// The pipeline's own readiness report (top-level sibling of `unreal`). When the mesh stage
	// produced nothing it records why (e.g. "mesh_not_produced"); surface that so a Mesh request
	// can explain the absence instead of dead-ending. `reason` rides only the absent paths — the
	// schema requires it whenever `present` is false and forbids it otherwise, so an empty string
	// here means "the mesh is present" rather than "the bundle forgot to say".
	//
	// MPB 1.0.0 folded readiness into the host block: the per-host `dcc_readiness.unreal` of the
	// integer era now lives at `hosts.unreal.readiness`, inside the one subtree this host reads
	// (HPS-33). The retired top-level `dcc_readiness` is NOT consulted in either of its old shapes
	// — a fallback there is how a clean break quietly becomes dual-parsing, and the version gate
	// above has already refused every bundle that would carry one.
	if (const TSharedPtr<FJsonObject>* HostsPtr = GetObject(Root, TEXT("hosts")))
	{
		if (const TSharedPtr<FJsonObject>* UnrealHostPtr = GetObject(*HostsPtr, TEXT("unreal")))
		{
			if (const TSharedPtr<FJsonObject>* ReadinessPtr = GetObject(*UnrealHostPtr, TEXT("readiness")))
			{
				if (const TSharedPtr<FJsonObject>* MeshImportPtr = GetObject(*ReadinessPtr, TEXT("mesh_import")))
				{
					M.MeshAbsentReason = GetString(*MeshImportPtr, TEXT("reason"));
				}
			}
		}
	}

	// Native Cesium artifacts + AOI bbox (top-level siblings of `unreal`). The bundle ships a
	// Cesium-ready quantized-mesh tileset (layout.cesiumTerrain) + tiled imagery (layout.imagery); the
	// local tile server hosts them so Cesium for Unreal can stream the bundle. Optional — absent on
	// bundles that predate the Cesium-terrain stage.
	if (const TSharedPtr<FJsonObject>* BboxPtr = GetObject(Root, TEXT("bbox")))
	{
		const TSharedPtr<FJsonObject> Bbox = *BboxPtr;
		M.BboxWestDeg = GetNumber(Bbox, TEXT("west"));
		M.BboxSouthDeg = GetNumber(Bbox, TEXT("south"));
		M.BboxEastDeg = GetNumber(Bbox, TEXT("east"));
		M.BboxNorthDeg = GetNumber(Bbox, TEXT("north"));
		M.bHasBbox = (M.BboxEastDeg > M.BboxWestDeg) && (M.BboxNorthDeg > M.BboxSouthDeg);
	}
	if (const TSharedPtr<FJsonObject>* LayoutPtr = GetObject(Root, TEXT("layout")))
	{
		const TSharedPtr<FJsonObject> Layout = *LayoutPtr;
		M.CesiumTerrainPath = GetString(Layout, TEXT("cesium_terrain"));
	}
	if (const TSharedPtr<FJsonObject>* CesiumTerrainPtr = GetObject(Root, TEXT("cesium_terrain")))
	{
		M.CesiumTerrainTileCount = GetInt(*CesiumTerrainPtr, TEXT("tile_count"));
	}
	M.bHasCesiumTerrain = !M.CesiumTerrainPath.IsEmpty();

	// Vault pick-and-process: a base_on_demand marker bundle ships no `unreal` block yet —
	// its Unreal formats generate later, on demand.
	if (const TSharedPtr<FJsonObject>* PackagingPtr = GetObject(Root, TEXT("packaging")))
	{
		M.DeliveryModel = GetString(*PackagingPtr, TEXT("delivery_model"));
	}

	// Road splines: the derived "road_splines" layer of the ODbL Vector/ set (top-level sibling of
	// `unreal`). The importer consumes the GeoJSON format only; a gpkg-only bundle (base tier) is
	// treated as not shipping splines. Read before the unreal-presence check so streaming-adjacent
	// data survives on unmaterialized bundles, mirroring the Cesium fields above.
	if (const TSharedPtr<FJsonObject>* VectorPtr = GetObject(Root, TEXT("vector")))
	{
		const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
		if ((*VectorPtr)->TryGetArrayField(TEXT("layers"), Layers) && Layers != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& LayerValue : *Layers)
			{
				const TSharedPtr<FJsonObject> Layer = LayerValue->AsObject();
				if (!Layer.IsValid() || GetString(Layer, TEXT("name")) != TEXT("road_splines"))
				{
					continue;
				}
				const TArray<TSharedPtr<FJsonValue>>* Formats = nullptr;
				if (Layer->TryGetArrayField(TEXT("formats"), Formats) && Formats != nullptr)
				{
					for (const TSharedPtr<FJsonValue>& FormatValue : *Formats)
					{
						const TSharedPtr<FJsonObject> Format = FormatValue->AsObject();
						if (Format.IsValid() && GetString(Format, TEXT("format")) == TEXT("geojson"))
						{
							M.RoadSplinesPath = GetString(Format, TEXT("path"));
							M.RoadSplinesSha256 = GetString(Format, TEXT("sha256"));
							break;
						}
					}
				}
				break;
			}
		}
	}
	M.bHasRoadSplines = !M.RoadSplinesPath.IsEmpty();

	// `hosts.unreal` — the one subtree this host reads, and never a sibling's (HPS-33).
	const TSharedPtr<FJsonObject>* UnrealPtr = nullptr;
	if (const TSharedPtr<FJsonObject>* HostsRootPtr = GetObject(Root, TEXT("hosts")))
	{
		UnrealPtr = GetObject(*HostsRootPtr, TEXT("unreal"));
	}
	if (UnrealPtr == nullptr)
	{
		// A missing `hosts.unreal` block does NOT mean "unmaterialized" — web can materialize a
		// bundle for another host only (a `hosts.revit` block, vector layers) and this
		// host still has nothing to import. IsBundleMaterialized decides which of the two this is
		// from the neutral signals, never from our own content (HPS-47); either way the shape is
		// the same refusal — the top-level facts above stay parsed (HPS-37) and the user is guided
		// to materialize the Unreal scope, keeping the Cesium-streaming fields intact for the
		// interim preview.
		OutError = IsBundleMaterialized(Root)
			? TEXT("This bundle hasn't generated its Unreal formats yet (it carries formats for other "
			       "tools, but nothing Unreal imports). Open your vault at mantle.place/vault, choose "
			       "\"Unreal Engine\" (or Generate all), then re-download. You can still preview it now "
			       "with \"Stream into Cesium\" (this bundle ships Cesium terrain).")
			: TEXT("This bundle hasn't generated its Unreal formats yet. Open your vault at "
			       "mantle.place/vault, choose \"Unreal Engine\" (or Generate all), then re-download. "
			       "You can still preview it now with \"Stream into Cesium\" (this bundle ships Cesium terrain).");
		return M;
	}
	const TSharedPtr<FJsonObject> Unreal = *UnrealPtr;

	// --- Heightmap ---------------------------------------------------------------------
	if (const TSharedPtr<FJsonObject>* HeightmapPtr = GetObject(Unreal, TEXT("heightmap")))
	{
		const TSharedPtr<FJsonObject> H = *HeightmapPtr;
		M.HeightmapPath = GetString(H, TEXT("path"));
		M.Resolution = GetInt(H, TEXT("resolution"));
		M.bRow0IsNorth = GetBool(H, TEXT("row0_is_north"), true);
		M.HeightmapSha256 = GetString(H, TEXT("sha256")); // verified against the extracted bytes at import

		if (const TSharedPtr<FJsonObject>* MapPtr = GetObject(H, TEXT("uint16_mapping")))
		{
			M.ZMinM = GetNumber(*MapPtr, TEXT("z_min_m"));
			M.ZMaxM = GetNumber(*MapPtr, TEXT("z_max_m"));
		}

		if (const TSharedPtr<FJsonObject>* XformPtr = GetObject(H, TEXT("landscape_transform")))
		{
			const TSharedPtr<FJsonObject> X = *XformPtr;
			M.ScaleXPercent = GetNumber(X, TEXT("scale_x_percent"));
			M.ScaleYPercent = GetNumber(X, TEXT("scale_y_percent"));
			M.ScaleZPercent = GetNumber(X, TEXT("z_scale_percent"));
			M.LocationZOffsetCm = GetNumber(X, TEXT("location_z_offset_cm"));
			M.SectionSizeQuads = GetInt(X, TEXT("section_size_quads"));
			M.SectionsPerComponent = GetInt(X, TEXT("sections_per_component"));
			M.ComponentCountX = GetInt(X, TEXT("component_count_x"));
			M.ComponentCountY = GetInt(X, TEXT("component_count_y"));
		}

		M.bHasHeightmap = !M.HeightmapPath.IsEmpty() && M.Resolution > 0
			&& M.SectionSizeQuads > 0 && M.SectionsPerComponent > 0
			&& M.ComponentCountX > 0 && M.ComponentCountY > 0;

		// The v17 schema requires heightmap.sha256; with pre-v17 tolerance gone, an absent hash is a
		// malformed manifest, not a legacy bundle — fail closed rather than silently skip the check.
		if (M.bHasHeightmap && M.HeightmapSha256.IsEmpty())
		{
			OutError = TEXT("manifest unreal.heightmap has no sha256 (required since v17); refusing to import unverifiable bytes.");
			return M;
		}
	}

	// --- Georeference ------------------------------------------------------------------
	if (const TSharedPtr<FJsonObject>* GeoPtr = GetObject(Unreal, TEXT("georeference")))
	{
		const TSharedPtr<FJsonObject> Geo = *GeoPtr;
		// EPSG is provenance-only on the consumer side: the contract places the AOI in a flat frame at world
		// origin and deliberately instantiates no AGeoReferencingSystem / no LWC in v1, so "EPSG parsed
		// but otherwise unused" is by design, not a bug.
		M.Epsg = ParseEpsg(GetString(Geo, TEXT("crs_projected")));
		if (const TSharedPtr<FJsonObject>* OriginPtr = GetObject(Geo, TEXT("origin")))
		{
			const TSharedPtr<FJsonObject> Origin = *OriginPtr;
			M.GroundOrthometricHM = GetNumber(Origin, TEXT("ground_orthometric_h_m"));
			if (const TSharedPtr<FJsonObject>* UtmPtr = GetObject(Origin, TEXT("utm")))
			{
				M.OriginEastingM = GetNumber(*UtmPtr, TEXT("easting_m"));
				M.OriginNorthingM = GetNumber(*UtmPtr, TEXT("northing_m"));
				if (M.Epsg == 0)
				{
					M.Epsg = GetInt(*UtmPtr, TEXT("epsg"));
				}
			}
		}

		// Enforce the v1 contract: the importer supports only the flat planet shape. Reject any
		// other declared value fail-closed (absent => legacy bundle, defaults to Flat).
		M.PlanetShape = GetString(Geo, TEXT("planet_shape"));
		if (!M.PlanetShape.IsEmpty() && !M.PlanetShape.Equals(TEXT("Flat")))
		{
			OutError = FString::Printf(
				TEXT("Unsupported planet_shape \"%s\": the v1 importer supports only \"Flat\"."),
				*M.PlanetShape);
			return M; // bValid stays false
		}
	}

	// --- Imagery drape -----------------------------------------------------------------
	if (const TSharedPtr<FJsonObject>* DrapePtr = GetObject(Unreal, TEXT("imagery_drape")))
	{
		const TSharedPtr<FJsonObject> D = *DrapePtr;
		M.DrapePath = GetString(D, TEXT("source"));
		M.DrapeSha256 = GetString(D, TEXT("sha256")); // verified against the extracted bytes at import
		const TArray<TSharedPtr<FJsonValue>>* Extent = nullptr;
		if (D->TryGetArrayField(TEXT("extent"), Extent) && Extent != nullptr && Extent->Num() == 4)
		{
			M.DrapeLeftM = (*Extent)[0]->AsNumber();
			M.DrapeBottomM = (*Extent)[1]->AsNumber();
			M.DrapeRightM = (*Extent)[2]->AsNumber();
			M.DrapeTopM = (*Extent)[3]->AsNumber();
			M.bHasDrape = !M.DrapePath.IsEmpty() && (M.DrapeRightM > M.DrapeLeftM) && (M.DrapeTopM > M.DrapeBottomM);
		}

		// imagery_drape.sha256 is likewise schema-required at v17 — same fail-closed rule as the heightmap.
		if (M.bHasDrape && M.DrapeSha256.IsEmpty())
		{
			OutError = TEXT("manifest unreal.imagery_drape has no sha256 (required since v17); refusing to import unverifiable bytes.");
			return M;
		}
	}

	// --- Mesh alternative --------------------------------------------------------------
	if (const TSharedPtr<FJsonObject>* MeshPtr = GetObject(Unreal, TEXT("mesh_alternative")))
	{
		const TSharedPtr<FJsonObject> Mesh = *MeshPtr;
		M.MeshPath = GetString(Mesh, TEXT("path"));
		M.MeshUpAxis = GetString(Mesh, TEXT("up_axis"));
		M.bNaniteRecommended = GetBool(Mesh, TEXT("nanite_recommended"), false);
		// The schema keeps mesh_alternative.sha256 optional (unlike heightmap/drape), so absent =
		// integrity check skipped rather than a parse failure.
		M.MeshSha256 = GetString(Mesh, TEXT("sha256"));
		M.bHasMesh = !M.MeshPath.IsEmpty();

		if (!CheckUpAxis(TEXT("mesh_alternative"), M.MeshUpAxis, OutError))
		{
			return M; // bValid stays false
		}
	}

	// --- Foliage points (-> DataTable scatter input; HPS-32) ----------------------------
	// The tree-points layer's OWN pointer block. v19 also carries `layout.tree_points` (a bare
	// string) and `landcover.tree_points` (richer, with a sha256) — HPS-33 says read your own
	// block regardless, so neither of those is a fallback for a missing unreal.foliage_points; an
	// absent pointer means the bundle simply has no tree points, not a parse failure.
	if (const TSharedPtr<FJsonObject>* FoliagePointsPtr = GetObject(Unreal, TEXT("foliage_points")))
	{
		M.FoliagePointsPath = GetString(*FoliagePointsPtr, TEXT("path"));
	}
	M.bHasFoliagePoints = !M.FoliagePointsPath.IsEmpty();

	// --- Landscape layers ---------------------------------------------------------------
	// The whole block, not just the one entry the importer applies. It is addressed to this host by
	// name, so leaving parts unparsed is how "published and ignored" happens a second time.
	// Keys are read off the object and sorted: the block is `additionalProperties: true`, so a
	// future ninth layer arrives without a code change, and JSON object order is not a contract.
	if (const TSharedPtr<FJsonObject>* LandscapeLayersPtr = GetObject(Unreal, TEXT("landscape_layers")))
	{
		TArray<FString> LayerNames;
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*LandscapeLayersPtr)->Values)
		{
			LayerNames.Add(Pair.Key);
		}
		LayerNames.Sort();

		for (const FString& LayerName : LayerNames)
		{
			const TSharedPtr<FJsonObject>* EntryPtr = GetObject(*LandscapeLayersPtr, *LayerName);
			if (EntryPtr == nullptr)
			{
				continue; // not an object; the schema says it must be, so there is nothing to read
			}
			const TSharedPtr<FJsonObject> Entry = *EntryPtr;

			FMantlePlaceLandscapeLayer Layer;
			Layer.Name = LayerName;
			Layer.Path = GetString(Entry, TEXT("path"));
			Layer.Sha256 = GetString(Entry, TEXT("sha256"));
			Layer.Materials = GetStrings(Entry, TEXT("materials"));

			const TArray<TSharedPtr<FJsonValue>>* UeReady = nullptr;
			if (Entry->TryGetArrayField(TEXT("ue_ready"), UeReady) && UeReady != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& Value : *UeReady)
				{
					const TSharedPtr<FJsonObject> RasterObject = Value.IsValid() ? Value->AsObject() : nullptr;
					if (!RasterObject.IsValid())
					{
						continue;
					}
					FMantlePlaceUeReadyRaster Raster;
					Raster.Path = GetString(RasterObject, TEXT("path"));
					Raster.Sha256 = GetString(RasterObject, TEXT("sha256"));
					Raster.Encoding = GetString(RasterObject, TEXT("encoding"));
					Raster.Width = GetInt(RasterObject, TEXT("width"));
					Raster.Height = GetInt(RasterObject, TEXT("height"));
					Raster.SizeBytes = static_cast<int64>(GetNumber(RasterObject, TEXT("size_bytes")));
					if (const TSharedPtr<FJsonObject>* MappingPtr = GetObject(RasterObject, TEXT("value_mapping")))
					{
						Raster.ValueMapping = ParseValueMapping(*MappingPtr);
					}
					Layer.UeReady.Add(MoveTemp(Raster));
				}
			}
			M.LandscapeLayers.Add(MoveTemp(Layer));
		}
	}
	M.bHasLandscapeLayers = M.LandscapeLayers.Num() > 0;

	// --- Buildings mesh ----------------------------------------------------------------
	// unreal.buildings_mesh is present iff the ETL emitted Mesh/Buildings.glb (extruded massing that
	// shares the terrain's Local Projected Frame). It carries the path + up_axis but no hash; the
	// integrity hash lives in the top-level `buildings.formats[]` entry (matched by path below).
	if (const TSharedPtr<FJsonObject>* BuildingsMeshPtr = GetObject(Unreal, TEXT("buildings_mesh")))
	{
		const TSharedPtr<FJsonObject> B = *BuildingsMeshPtr;
		M.BuildingsPath = GetString(B, TEXT("path"));
		M.BuildingsUpAxis = GetString(B, TEXT("up_axis"));
		M.bHasBuildings = !M.BuildingsPath.IsEmpty();

		if (!CheckUpAxis(TEXT("buildings_mesh"), M.BuildingsUpAxis, OutError))
		{
			return M; // bValid stays false
		}
	}
	if (M.bHasBuildings)
	{
		// Optional: an absent/empty hash makes the importer skip the buildings integrity check (as with
		// legacy bundles), rather than fail-closing.
		if (const TSharedPtr<FJsonObject>* BuildingsPtr = GetObject(Root, TEXT("buildings")))
		{
			const TArray<TSharedPtr<FJsonValue>>* Formats = nullptr;
			if ((*BuildingsPtr)->TryGetArrayField(TEXT("formats"), Formats) && Formats != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& Entry : *Formats)
				{
					const TSharedPtr<FJsonObject> Obj = Entry->AsObject();
					if (Obj.IsValid() && GetString(Obj, TEXT("path")) == M.BuildingsPath)
					{
						M.BuildingsSha256 = GetString(Obj, TEXT("sha256"));
						break;
					}
				}
			}
		}
	}

	// Consistency: the recommended-size formula must reproduce the stated resolution.
	if (M.bHasHeightmap)
	{
		const int32 Derived = M.ComponentCountX * M.GetQuadsPerComponent() + 1;
		if (Derived != M.Resolution)
		{
			OutError = FString::Printf(
				TEXT("Heightmap resolution %d != component_count_x(%d)*quads_per_component(%d)+1 = %d."),
				M.Resolution, M.ComponentCountX, M.GetQuadsPerComponent(), Derived);
			return M;
		}
	}

	M.bValid = M.bHasHeightmap || M.bHasMesh;
	if (!M.bValid)
	{
		// An empty-but-present `unreal` block on a base_on_demand bundle means the same thing as an
		// absent one: the Unreal formats just haven't been generated yet — give the vault guidance,
		// not a malformed-manifest message.
		if (M.DeliveryModel == TEXT("base_on_demand"))
		{
			OutError = TEXT("This bundle hasn't generated its Unreal formats yet. Open your vault at "
			                "mantle.place/vault, choose \"Unreal Engine\" (or Generate all), then re-download. "
			                "You can still preview it now with \"Stream into Cesium\" (this bundle ships Cesium terrain).");
		}
		else
		{
			OutError = TEXT("manifest \"unreal\" block has neither a usable heightmap nor a mesh.");
		}
	}
	return M;
}
