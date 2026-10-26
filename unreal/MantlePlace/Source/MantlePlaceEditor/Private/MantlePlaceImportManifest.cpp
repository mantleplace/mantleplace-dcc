// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportManifest.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceRoadSplinesLogic.h"    // FMantlePlaceRoadSplinesDatum
#include "MantlePlaceTreePointsLogic.h"     // FMantlePlaceTreePointsFrame
#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion (the MPB clean-break floor)

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
		Heightmap.ComponentCountY * GetQuadsPerComponent() * Heightmap.ScaleYPercent,   // North span
		Heightmap.ComponentCountX * GetQuadsPerComponent() * Heightmap.ScaleXPercent);  // East span
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
	return FVector(Heightmap.ScaleYPercent, Heightmap.ScaleXPercent, Heightmap.ScaleZPercent);
}

FVector FMantlePlaceVaultManifest::GetLandscapeSpawnLocation() const
{
	// Mirror the engine's New-Landscape centring: the actor location is the corner; offset it
	// by half the (scaled) quad span so the AOI centre lands on world (0,0). Identity rotation,
	// so TransformVector(scale) is a component-wise multiply.
	const FVector2D AoiSize = GetAoiSizeUeCm();
	return FVector(-AoiSize.X / 2.0, -AoiSize.Y / 2.0, Heightmap.LocationZOffsetCm);
}

FVector FMantlePlaceVaultManifest::GetMeshLocation() const
{
	// The glb is centroid-local with z=0 at centroid ground; place that at true elevation so it
	// overlays the Landscape (which encodes true orthometric Z too). XY centroid -> world (0,0).
	return FVector(0.0, 0.0, Georef.GroundOrthometricHM * 100.0);
}

void FMantlePlaceVaultManifest::GetDrapeWorldRect(FVector2D& OutMin, FVector2D& OutSize) const
{
	// UTM -> UE world cm relative to the georeference origin (North -> +X, East -> +Y).
	const FVector Min = ProjectedToUeCm(Drape.LeftM - Georef.OriginEastingM, Drape.BottomM - Georef.OriginNorthingM, 0.0);
	const FVector Size = ProjectedToUeCm(Drape.RightM - Drape.LeftM, Drape.TopM - Drape.BottomM, 0.0);
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
	return LandscapeLayers.Layers.FindByPredicate(
		[Name](const FMantlePlaceLandscapeLayer& Layer) { return Layer.Name == Name; });
}

FMantlePlaceTreePointsFrame FMantlePlaceVaultManifest::GetFoliagePointsFrame() const
{
	// The first MPB version whose `foliage_points` must state its frame. A version below it is a
	// bundle built before the format could say, and only there is the extent substitute the whole
	// of the check (HPS-53).
	static const FString FirstVersionStatingTreePointsFrame = TEXT("1.3.0");

	FMantlePlaceTreePointsFrame Frame;
	Frame.Crs = Foliage.Crs;
	Frame.Units = Foliage.Units;
	Frame.HorizontalUnits = Foliage.HorizontalUnits;
	Frame.HostCrs = Georef.CrsProjected;
	Frame.bRequired = !MantlePlaceIsManifestVersionBelowFloor(Version, FirstVersionStatingTreePointsFrame);
	return Frame;
}

FMantlePlaceRoadSplinesDatum FMantlePlaceVaultManifest::GetRoadSplinesDatum() const
{
	// The first MPB version whose layers state the datum of their heights. Below it, every height a
	// bundle carried was EGM2008, and a road layer that states none is one of those.
	static const FString FirstVersionStatingHeightDatum = TEXT("1.8.0");

	FMantlePlaceRoadSplinesDatum Datum;
	Datum.Stated = RoadSplines.VerticalDatum;
	Datum.bRequired = !MantlePlaceIsManifestVersionBelowFloor(Version, FirstVersionStatingHeightDatum);
	return Datum;
}

TArray<FMantlePlaceRoadSplinesCandidate> FMantlePlaceVaultManifest::GetRoadSplinesPlan() const
{
	return FMantlePlaceRoadSplinesLogic::PlanSources(
	    RoadSplines.bEnginePresent, RoadSplines.EngineVerticalDatum,
	    RoadSplines.bPresent || RoadSplines.bGpkgPresent, GetRoadSplinesDatum());
}

// ── Parsing ────────────────────────────────────────────────────────────────

using MantlePlaceManifestJson::GetInt;
using MantlePlaceManifestJson::GetNumber;
using MantlePlaceManifestJson::GetObject;
using MantlePlaceManifestJson::GetString;

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

namespace
{
/** `hosts.unreal.georeference`: this host's frame. Not an artifact, so Parse reads it itself. */
bool ParseGeoreference(const TSharedPtr<FJsonObject>& Unreal, FMantlePlaceGeoreferenceBlock& Georef, FString& OutError)
{
	const TSharedPtr<FJsonObject>* GeoPtr = GetObject(Unreal, TEXT("georeference"));
	if (GeoPtr == nullptr)
	{
		return true;
	}
	const TSharedPtr<FJsonObject> Geo = *GeoPtr;
	// EPSG is provenance-only on the consumer side: the contract places the AOI in a flat frame at world
	// origin and deliberately instantiates no AGeoReferencingSystem / no LWC in v1, so "EPSG parsed
	// but otherwise unused" is by design, not a bug.
	Georef.CrsProjected = GetString(Geo, TEXT("crs_projected"));
	Georef.Epsg = MantlePlaceImportManifest::ParseEpsg(Georef.CrsProjected);
	if (const TSharedPtr<FJsonObject>* OriginPtr = GetObject(Geo, TEXT("origin")))
	{
		const TSharedPtr<FJsonObject> Origin = *OriginPtr;
		Georef.GroundOrthometricHM = GetNumber(Origin, TEXT("ground_orthometric_h_m"));
		if (const TSharedPtr<FJsonObject>* UtmPtr = GetObject(Origin, TEXT("utm")))
		{
			Georef.OriginEastingM = GetNumber(*UtmPtr, TEXT("easting_m"));
			Georef.OriginNorthingM = GetNumber(*UtmPtr, TEXT("northing_m"));
			if (Georef.Epsg == 0)
			{
				Georef.Epsg = GetInt(*UtmPtr, TEXT("epsg"));
			}
		}
	}

	// Enforce the v1 contract: the importer supports only the flat planet shape. Reject any
	// other declared value fail-closed (absent => legacy bundle, defaults to Flat).
	Georef.PlanetShape = GetString(Geo, TEXT("planet_shape"));
	if (!Georef.PlanetShape.IsEmpty() && !Georef.PlanetShape.Equals(TEXT("Flat")))
	{
		OutError = FString::Printf(
			TEXT("Unsupported planet_shape \"%s\": the v1 importer supports only \"Flat\"."),
			*Georef.PlanetShape);
		return false;
	}
	return true;
}

/** The bundle's native Cesium artifacts + AOI bbox (top-level siblings of `hosts`). The bundle ships a
 *  Cesium-ready quantized-mesh tileset (layout.cesium_terrain); the local tile server hosts it so
 *  Cesium for Unreal can stream the bundle. Optional — absent on bundles that predate the stage. */
void ParseCesium(const TSharedPtr<FJsonObject>& Root, FMantlePlaceCesiumBlock& Cesium)
{
	if (const TSharedPtr<FJsonObject>* BboxPtr = GetObject(Root, TEXT("bbox")))
	{
		const TSharedPtr<FJsonObject> Bbox = *BboxPtr;
		Cesium.BboxWestDeg = GetNumber(Bbox, TEXT("west"));
		Cesium.BboxSouthDeg = GetNumber(Bbox, TEXT("south"));
		Cesium.BboxEastDeg = GetNumber(Bbox, TEXT("east"));
		Cesium.BboxNorthDeg = GetNumber(Bbox, TEXT("north"));
		Cesium.bHasBbox = (Cesium.BboxEastDeg > Cesium.BboxWestDeg) && (Cesium.BboxNorthDeg > Cesium.BboxSouthDeg);
	}
	if (const TSharedPtr<FJsonObject>* LayoutPtr = GetObject(Root, TEXT("layout")))
	{
		Cesium.TerrainPath = GetString(*LayoutPtr, TEXT("cesium_terrain"));
	}
	if (const TSharedPtr<FJsonObject>* CesiumTerrainPtr = GetObject(Root, TEXT("cesium_terrain")))
	{
		Cesium.TerrainTileCount = GetInt(*CesiumTerrainPtr, TEXT("tile_count"));
	}
	Cesium.bHasTerrain = !Cesium.TerrainPath.IsEmpty();
}

/** `delivery`: display only (see FMantlePlaceDeliveryFacts). Verbatim, never validated -- nothing
 *  reads it to place, so an unknown value is a word the user is shown, not a refusal. */
void ParseDelivery(const TSharedPtr<FJsonObject>& Root, FMantlePlaceDeliveryFacts& Delivery)
{
	const TSharedPtr<FJsonObject>* DeliveryPtr = GetObject(Root, TEXT("delivery"));
	if (DeliveryPtr == nullptr)
	{
		return;
	}
	Delivery.bDeclared = true;
	Delivery.UnitSystem = GetString(*DeliveryPtr, TEXT("unit_system"));
	Delivery.LinearUnit = GetString(*DeliveryPtr, TEXT("linear_unit"));
	Delivery.Label = GetString(*DeliveryPtr, TEXT("label"));
	double HorizontalEpsg = 0.0;
	// A JSON null reads as no number, which is what the `local_ft` tier means by it.
	Delivery.bHasHorizontalEpsg = (*DeliveryPtr)->TryGetNumberField(TEXT("horizontal_epsg"), HorizontalEpsg);
	Delivery.HorizontalEpsg = Delivery.bHasHorizontalEpsg ? FMath::RoundToInt(HorizontalEpsg) : 0;
}
}

namespace
{
/** `file_entries` (MPB 1.14.0): each file's ODbL role, licence and sources. Only those three members are
 *  read; the frame members are the host blocks' to state (HPS-33). A malformed entry is skipped, and a
 *  file with no entry reads as `database` (MantlePlaceOdblLogic::RoleOfPath), the stricter duty. */
void ParseFileLicensing(const TSharedPtr<FJsonObject>& Root, TMap<FString, FMantlePlaceFileLicensing>& Out)
{
	const TSharedPtr<FJsonObject>* EntriesPtr = GetObject(Root, TEXT("file_entries"));
	if (EntriesPtr == nullptr)
	{
		return;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*EntriesPtr)->Values)
	{
		const TSharedPtr<FJsonObject>* EntryPtr = nullptr;
		if (!Pair.Value.IsValid() || !Pair.Value->TryGetObject(EntryPtr) || EntryPtr == nullptr)
		{
			continue;
		}
		FMantlePlaceFileLicensing Entry;
		const bool bRoleStated = (*EntryPtr)->HasTypedField<EJson::String>(TEXT("odbl_role"));
		Entry.Role = MantlePlaceOdblLogic::ParseRole(bRoleStated, GetString(*EntryPtr, TEXT("odbl_role")));
		Entry.License = GetString(*EntryPtr, TEXT("license"));
		const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
		if ((*EntryPtr)->TryGetArrayField(TEXT("sources"), Sources) && Sources != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& SourceValue : *Sources)
			{
				const TSharedPtr<FJsonObject>* SourcePtr = nullptr;
				if (SourceValue.IsValid() && SourceValue->TryGetObject(SourcePtr) && SourcePtr != nullptr)
				{
					Entry.Sources.Add({ GetString(*SourcePtr, TEXT("name")), GetString(*SourcePtr, TEXT("license")) });
				}
			}
		}
		Out.Add(Pair.Key, MoveTemp(Entry));
	}
}

/** `attribution.sources[]`: the credit the manifest words, kept verbatim for MantlePlaceOdblLogic::CreditLine. */
void ParseAttribution(const TSharedPtr<FJsonObject>& Root, TArray<FMantlePlaceAttributionSource>& Out)
{
	const TSharedPtr<FJsonObject>* AttributionPtr = GetObject(Root, TEXT("attribution"));
	const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
	if (AttributionPtr == nullptr || !(*AttributionPtr)->TryGetArrayField(TEXT("sources"), Sources) || Sources == nullptr)
	{
		return;
	}
	for (const TSharedPtr<FJsonValue>& SourceValue : *Sources)
	{
		const TSharedPtr<FJsonObject>* SourcePtr = nullptr;
		if (SourceValue.IsValid() && SourceValue->TryGetObject(SourcePtr) && SourcePtr != nullptr)
		{
			FMantlePlaceAttributionSource Source;
			Source.ProviderId = GetString(*SourcePtr, TEXT("provider_id"));
			Source.AttributionText = GetString(*SourcePtr, TEXT("attribution_text"));
			Source.License = GetString(*SourcePtr, TEXT("license"));
			Source.LicenseUrl = GetString(*SourcePtr, TEXT("license_url"));
			Out.Add(MoveTemp(Source));
		}
	}
}

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
	// fields are ignored, so any minor of a major this reader speaks reads here; an unknown higher
	// MAJOR is a graceful refusal rather than a best-effort parse, because a major is exactly the
	// promise that something this reader relies on may have changed meaning. This is a refusal, not
	// a crash, and it names the version it saw so the user can tell "too new" from "too old" — the
	// two failures a bare "unsupported" message makes indistinguishable.
	//
	// This reader speaks 1.x and 2.x (MantlePlaceMaxSupportedManifestMajor): a 2.0 bundle changed the
	// folders and the names, and every file here is found through a pointer value, so the same
	// parse reads both. What a 2.0 bundle moved is read where it moved to (the heightmap's and the
	// UE-ready rasters' value mappings, in `file_entries`), never by branching on the version.
	if (Parsed.Major > MantlePlaceMaxSupportedManifestMajor)
	{
		OutError = FString::Printf(
		    TEXT("Bundle manifest version %s is newer than this plugin understands (it reads up to %d.x). "
		         "Update the Mantle Place plugin to import this bundle."),
		    *M.Version, MantlePlaceMaxSupportedManifestMajor);
		return M;
	}

	// Top-level facts, read whether or not the bundle has been materialized for this host (HPS-37).
	ParseCesium(Root, M.Cesium);
	// Vault pick-and-process: a base_on_demand marker bundle ships no `unreal` block yet —
	// its Unreal formats generate later, on demand.
	if (const TSharedPtr<FJsonObject>* PackagingPtr = GetObject(Root, TEXT("packaging")))
	{
		M.DeliveryModel = GetString(*PackagingPtr, TEXT("delivery_model"));
	}
	ParseDelivery(Root, M.Delivery);
	ParseFileLicensing(Root, M.FileLicensing);
	ParseAttribution(Root, M.Attribution);

	// `hosts.unreal` — the one subtree this host reads, and never a sibling's (HPS-33).
	TSharedPtr<FJsonObject> Unreal;
	if (const TSharedPtr<FJsonObject>* HostsRootPtr = GetObject(Root, TEXT("hosts")))
	{
		if (const TSharedPtr<FJsonObject>* UnrealPtr = GetObject(*HostsRootPtr, TEXT("unreal")))
		{
			Unreal = *UnrealPtr;
		}
	}

	// Each artifact reads its own block. The handlers that read only top-level siblings (road
	// splines) run whether or not `hosts.unreal` exists, as they always have; the rest find no
	// block and leave theirs absent.
	const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
	const FMantlePlaceParseInput Input{ Root, Unreal };
	if (!ParseGeoreference(Unreal, M.Georef, OutError))
	{
		return M; // bValid stays false
	}
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Handler->ParseFields(Input, M, OutError))
		{
			return M; // bValid stays false
		}
	}

	if (!Unreal.IsValid())
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

	// Checks that need more than one block, once every block is read.
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Handler->CrossCheck(M, OutError))
		{
			return M; // bValid stays false
		}
	}

	M.bValid = M.Heightmap.bPresent || M.Mesh.bPresent;
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
