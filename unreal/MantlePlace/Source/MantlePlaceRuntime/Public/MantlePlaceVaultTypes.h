// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.generated.h"

/**
 * The minimum bundle-manifest version every native consumer accepts. v18 is the per-host hygiene
 * bump: `dcc_readiness` is normalized to per-host keys, so a
 * v17 reader of `dcc_readiness.mesh_import` must read `dcc_readiness.unreal.mesh_import`. That
 * relocation is the whole reason the floor moves — the `unreal` block's own keys are unchanged.
 * The floor is a clean break: pre-v18 bundles are rejected outright and re-procured, never
 * dual-parsed. One home for the number — the importer's manifest gate, the bundle cache's
 * validity decision, and tools/manifest-conformance (which regexes this header) all read it here.
 */
inline constexpr int32 MantlePlaceMinSupportedManifestVersion = 18;

/**
 * Status of an owned vault bundle, mirrored from the platform vault API
 *.
 * Only `Available` bundles are downloadable. Unknown future strings map to `Unknown`
 * (additive-contract friendly) rather than failing the parse.
 */
UENUM(BlueprintType)
enum class EMantlePlaceVaultBundleStatus : uint8
{
	/** Packaged and downloadable. */
	Available,
	/** A refresh/re-cut is in progress; not yet downloadable. */
	RefreshPending,
	/** The order was refunded; surfaced for state, not downloadable. */
	Refunded,
	/** The ETL job failed; surfaced for state, not downloadable. */
	Failed,
	/** Unrecognized status string (forward-compatibility). */
	Unknown
};

/**
 * Which layers a bundle's sidecar manifest reports. Only meaningful when the owning
 * item's bLayersKnown is true (legacy bundles have no sidecar - treat as "unknown,"
 * not "absent").
 */
USTRUCT(BlueprintType)
struct FMantlePlaceVaultLayers
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bImagery = false;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bBasemap = false;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bElevation = false;
};

/** A downloadable per-format artifact present in a bundle (from the list's download.formats). */
USTRUCT(BlueprintType)
struct FMantlePlaceVaultArtifact
{
	GENERATED_BODY()

	/** One of glb | fbx | geotiff | cog | dwg | pmtiles. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Format;

	/** Artifact size in bytes; 0 means "not recorded" (older orders), not a zero-byte file. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int64 ByteSize = 0;
};

/**
 * One owned bundle in the curator's vault, as returned by GET /api/v1/vault/bundles.
 *
 * The four sidecar fields (Layers / ManifestVersion / SizeBytes / Sha256) are "null =
 * unknown" on legacy bundles packaged before the sidecar producer shipped. Each carries
 * a bHas* / bLayersKnown companion: when false, treat the value as unknown (e.g. skip the
 * sha256 integrity check rather than failing it).
 */
USTRUCT(BlueprintType)
struct FMantlePlaceVaultItem
{
	GENERATED_BODY()

	/** Order id ("id") - the {orderId} path key for the download-mint endpoint. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString OrderId;

	/** Human label for the AOI ("aoiLabel"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString AoiLabel;

	/** ISO-8601 creation timestamp ("createdAt"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString CreatedAt;

	/** AOI area in km2 ("areaKm2"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	double AreaKm2 = 0.0;

	/** Order status; only Available is downloadable. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	EMantlePlaceVaultBundleStatus Status = EMantlePlaceVaultBundleStatus::Unknown;

	/** True when the sidecar reported layers (legacy bundles: false). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bLayersKnown = false;

	/** Layers present; meaningful only when bLayersKnown. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FMantlePlaceVaultLayers Layers;

	/** True when the sidecar reported a manifest version. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bHasManifestVersion = false;

	/** Bundle manifest version; meaningful only when bHasManifestVersion. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int32 ManifestVersion = 0;

	/** True when the sidecar reported a whole-bundle size. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bHasSizeBytes = false;

	/** Whole-bundle (download.zip) size in bytes; meaningful only when bHasSizeBytes. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	int64 SizeBytes = 0;

	/** True when the sidecar reported a whole-bundle sha256. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bHasSha256 = false;

	/** Whole-bundle integrity hash; meaningful only when bHasSha256. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Sha256;

	/** Formats present in the bundle ("formats"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	TArray<FString> Formats;

	/** Per-format downloadable artifacts ("download.formats"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	TArray<FMantlePlaceVaultArtifact> DownloadFormats;
};

/**
 * A freshly minted presigned download, from POST /api/v1/vault/bundles/{orderId}/download.
 * The URL is a direct R2 GET valid until ExpiresAt (24h TTL). Re-mint per import; never
 * cache the URL past ExpiresAt.
 */
USTRUCT(BlueprintType)
struct FMantlePlacePresignedDownload
{
	GENERATED_BODY()

	/** Presigned R2 GET URL ("url"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Url;

	/** ISO-8601 expiry ("expiresAt") - authoritative; do not use the URL past it. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString ExpiresAt;
};

/**
 * Lifecycle of an on-demand "generate Unreal formats" (materialize) job, tracked by polling
 * GET /api/v1/vault/bundles/{orderId}/materialize. A BASE bundle ships no Unreal block; asking
 * the vault to materialize the Unreal scope produces heightmap/drape/terrain-mesh (+ buildings)
 * so a native import becomes possible. Unrecognized future state strings map to Unknown
 * (additive-contract friendly) rather than failing the parse.
 */
UENUM(BlueprintType)
enum class EMantlePlaceMaterializeState : uint8
{
	/** Accepted / queued, not yet started. */
	Pending,
	/** Running (the ETL is generating the requested formats). */
	Processing,
	/** Done - the Unreal formats are ready and the bundle is downloadable. */
	Complete,
	/** The materialize job failed; surfaced for state, not downloadable. */
	Failed,
	/** Unrecognized status string (forward-compatibility). */
	Unknown
};

/**
 * What the platform did with a materialize request.
 *
 * Five wire shapes, four outcomes. Modelling this as "a job id, or an error" was wrong: two of the
 * five are successes that name no job at all, and reading their missing job id as a failure is what
 * left the Revit host unable to import any bundle that had nothing left to build.
 */
UENUM(BlueprintType)
enum class EMantlePlaceMaterializeStartOutcome : uint8
{
	/** A fresh job. Tokens is the effective set being built. */
	Started,
	/** A run was already in flight and this joined it. JobId MAY be empty - see ParseMaterializeStartResponse. */
	Joined,
	/** Nothing to build: everything asked for is delivered or can never be produced here. Tokens is what the bundle HAS. */
	NothingToDo,
	/** The order's core build has not finished; the picks are parked and fire on their own. No job exists yet. */
	Queued
};

/** The response to starting a materialize. */
USTRUCT(BlueprintType)
struct FMantlePlaceMaterializeStart
{
	GENERATED_BODY()

	/** Which of the four things happened. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	EMantlePlaceMaterializeStartOutcome Outcome = EMantlePlaceMaterializeStartOutcome::Started;

	/** The job to follow. Empty for every outcome except a started or joined run. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString JobId;

	/** The effective set being built, the delivered set, or the parked set, per Outcome. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	TArray<FString> Tokens;

	/** True when this joined a run rather than starting one. */
	bool IsAlreadyRunning() const
	{
		return Outcome == EMantlePlaceMaterializeStartOutcome::Joined;
	}
};

/** A requested deliverable this bundle will never carry, and the platform's reason. */
USTRUCT(BlueprintType)
struct FMantlePlaceMissingDeliverable
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Token;

	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Reason;
};

/**
 * A materialize status poll result. Fraction is in [0,1], or -1 when the endpoint reports no
 * measurable progress (indeterminate). Message/JobId are best-effort and may be empty.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceMaterializeStatus
{
	GENERATED_BODY()

	/** Current lifecycle state. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	EMantlePlaceMaterializeState State = EMantlePlaceMaterializeState::Unknown;

	/** Progress in [0,1]; -1 means "unknown / indeterminate". */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	float Fraction = -1.0f;

	/** Human-readable status/error detail, when the endpoint supplies one. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Message;

	/** The materialize jobId, when reported. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString JobId;

	/** Which of the REQUESTED tokens the bundle now carries. Empty on the legacy job-status shape. */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	TArray<FString> Delivered;

	/**
	 * Requested tokens the platform will never produce for this area, with its reason. A gap, not a
	 * failure: waiting for one is waiting forever, so these are reported and stepped over.
	 */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	TArray<FMantlePlaceMissingDeliverable> Unproducible;
};
