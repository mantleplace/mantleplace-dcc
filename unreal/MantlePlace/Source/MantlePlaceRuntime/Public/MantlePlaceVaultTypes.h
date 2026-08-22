// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.generated.h"

/**
 * The minimum bundle-manifest version every native consumer accepts.
 *
 * MPB 1.0.0 re-baselined the contract onto semantic versioning: the `version` field is the STRING
 * "1.0.0" where it used to be the integer 19, keys went snake_case, and everything host-specific
 * moved under `hosts.<hostId>`. The integer era (v7-v19) is pre-history — still published, never
 * read here.
 *
 * The floor is a clean break: everything below it is rejected outright and re-procured, never
 * dual-parsed (HPS-31). Crossing an era makes that stricter rather than looser, because an
 * integer-versioned bundle is not merely old, it is written in a dialect this reader does not
 * speak at all — `hosts.unreal` against a top-level `unreal` block.
 *
 * One home for the number: the importer's manifest gate, the bundle cache's validity decision, and
 * tools/manifest-conformance (which regexes this header) all read it here. The supported MAJOR
 * line is derived from this same literal rather than written twice.
 */
inline const FString MantlePlaceMinSupportedManifestVersion = TEXT("1.0.0");

/**
 * A parsed MPB manifest version.
 *
 * `bValid` is false for everything that is not `MAJOR.MINOR.PATCH` — an absent version, an integer
 * from the pre-history, a partial "1.0", a pre-release tag. Those are all refusals, and the reader
 * must never coerce them into a number: the integer era's reader read an absent version as 0 and
 * refused it, and reading a STRING as 0 the same way would be an accident that happens to work
 * rather than a decision.
 */
struct FMantlePlaceManifestVersion
{
	bool bValid = false;
	int32 Major = 0;
	int32 Minor = 0;
	int32 Patch = 0;

	/** Parse `MAJOR.MINOR.PATCH`. Never throws; anything else comes back `bValid == false`. */
	static FMantlePlaceManifestVersion Parse(const FString& Text)
	{
		FMantlePlaceManifestVersion Out;
		TArray<FString> Parts;
		Text.ParseIntoArray(Parts, TEXT("."), /*InCullEmpty=*/false);
		if (Parts.Num() != 3)
		{
			return Out;
		}
		for (const FString& Part : Parts)
		{
			// `IsNumeric` accepts a leading sign and a decimal point; neither belongs in a semver
			// component, and "1.-0.0" parsing as valid would be a silent wrong answer.
			//
			// A leading zero is rejected too ("01.0.0"), because semver forbids it and the
			// platform's own publisher and gate do. A component that parsed here but not there
			// would be a version this host imports and the contract does not admit exists.
			if (Part.IsEmpty() || !Part.IsNumeric() || Part.Contains(TEXT("-")) || Part.Contains(TEXT("+"))
			    || Part.Contains(TEXT(".")) || (Part.Len() > 1 && Part[0] == TEXT('0')))
			{
				return Out;
			}
		}
		Out.Major = FCString::Atoi(*Parts[0]);
		Out.Minor = FCString::Atoi(*Parts[1]);
		Out.Patch = FCString::Atoi(*Parts[2]);
		Out.bValid = true;
		return Out;
	}

	FString ToString() const
	{
		return bValid ? FString::Printf(TEXT("%d.%d.%d"), Major, Minor, Patch) : TEXT("(none)");
	}

	/** Total order over parsed versions. Only meaningful when both are valid. */
	bool operator<(const FMantlePlaceManifestVersion& Other) const
	{
		return Major != Other.Major   ? Major < Other.Major
		     : Minor != Other.Minor   ? Minor < Other.Minor
		                              : Patch < Other.Patch;
	}
};

/**
 * Is `Version` below `Floor`, across BOTH version families?
 *
 * The whole integer pre-history sorts below the whole semver era, so anything that is not a semver
 * string — an integer-era "19", an absent value, a malformed one — is below a semver floor. That
 * single rule is why the caller never has to know which era a stored version came from, which
 * matters most at the cache, where a sidecar written before the re-baseline sits on disk beside
 * one written after it.
 */
/**
 * A manifest version as it should READ to a user: "1.0.0" for the MPB era, "v19" for the
 * pre-history. The semver era's value carries no marker of its own, and the integer era's always
 * read with a leading `v`, so rendering both verbatim would relabel every pre-history bundle in the
 * vault listing. Display only — nothing gates on this.
 */
inline FString MantlePlaceDescribeManifestVersion(const FString& Version)
{
	if (Version.IsEmpty())
	{
		return TEXT("unknown");
	}
	return FMantlePlaceManifestVersion::Parse(Version).bValid ? Version : FString::Printf(TEXT("v%s"), *Version);
}

inline bool MantlePlaceIsManifestVersionBelowFloor(const FString& Version, const FString& Floor)
{
	const FMantlePlaceManifestVersion Parsed = FMantlePlaceManifestVersion::Parse(Version);
	const FMantlePlaceManifestVersion FloorParsed = FMantlePlaceManifestVersion::Parse(Floor);
	if (!FloorParsed.bValid)
	{
		// An unparseable floor is a programming error, not bundle data. Refuse nothing rather than
		// refuse everything: a cache that invalidated every entry on a typo'd constant would be a
		// far worse failure than one that stopped enforcing a rule it can no longer read.
		return false;
	}
	return !Parsed.bValid || Parsed < FloorParsed;
}

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

	/**
	 * Bundle manifest version as the sidecar reported it; meaningful only when
	 * bHasManifestVersion.
	 *
	 * A STRING, and deliberately not parsed here. The vault lists bundles at rest, which span both
	 * eras — a bundle cut before the MPB re-baseline reports the integer 19, one cut after reports
	 * "1.0.0" — and this field is surfaced to the user, not gated on. Rendering what the sidecar
	 * actually said keeps a pre-history bundle legible in the listing instead of showing 0 for a
	 * version that is merely from the other family (HPS-20: unknown is not zero).
	 */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString ManifestVersion;

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
};
