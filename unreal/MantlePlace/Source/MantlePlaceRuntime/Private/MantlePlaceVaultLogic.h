// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.h"

/**
 * Pure (engine-/network-free) logic for the Mantle Place vault API.
 *
 * Everything here is deterministic and headless-testable: URL/body construction and JSON
 * response parsing for the two plugin-facing endpoints - list owned bundles and mint a
 * presigned download URL. The UObject shim (UMantlePlaceVaultClient) owns the impure
 * parts: issuing the authenticated HTTP request and firing Blueprint events. Keep this
 * layer free of HTTP/UObject dependencies so the automation test can exercise it under
 * -nullrhi (mirrors FMantlePlaceAuthLogic).
 */
struct FMantlePlaceVaultLogic
{
	/** Strip whitespace and any trailing '/' from a configured base URL. */
	static FString NormalizeBaseUrl(const FString& BaseUrl);

	/** GET endpoint: list the curator's owned bundles. */
	static FString BuildListBundlesUrl(const FString& BaseUrl);

	/** POST endpoint: mint a presigned download URL for one order (orderId path segment, URL-encoded). */
	static FString BuildDownloadUrl(const FString& BaseUrl, const FString& OrderId);

	/** JSON request body for the download mint: {"format":"<fmt>"} (condensed). */
	static FString BuildDownloadBody(const FString& Format);

	/**
	 * Parse the list response: { "bundles": [ ... ] }. Returns true and fills OutItems on
	 * success. Applies the "null = unknown" rule (per-field bHas* companions). A single
	 * malformed entry (non-object, or missing the required "id") is SKIPPED rather than
	 * failing the whole list, so one bad row can't blank the curator's vault; each skip is
	 * appended to OutWarnings when provided. Fails closed (false, fills OutError) only on
	 * invalid JSON or a missing/non-array top-level "bundles".
	 */
	static bool ParseListResponse(const FString& JsonStr, TArray<FMantlePlaceVaultItem>& OutItems, FString& OutError, TArray<FString>* OutWarnings = nullptr);

	/**
	 * Parse the download-mint response: { "url", "expiresAt" }. Returns true on success.
	 * Fail-closed if "url" is missing/empty (falls back to error-body parsing for the message).
	 */
	static bool ParseDownloadResponse(const FString& JsonStr, FMantlePlacePresignedDownload& OutDownload, FString& OutError);

	/**
	 * Parse a platform error body into a message, in the one precedence order every platform
	 * error-body parser shares (HPS-48): error_description, msg, message, error_code, error.
	 * Returns false if no message present. OutCode is set to the optional "code" (e.g.
	 * "refunded"/"revoked") — a separate read, unaffected by the message precedence.
	 */
	static bool ParseErrorBody(const FString& JsonStr, FString& OutError, FString& OutCode);

	/** Map a status string to the enum; unrecognized strings -> Unknown (not a parse error). */
	static EMantlePlaceVaultBundleStatus ParseStatus(const FString& Status);

	/** True iff the item can be presigned (Status == Available). */
	static bool IsDownloadable(const FMantlePlaceVaultItem& Item);

	/** True iff Format is one of the six known artifact formats (case-insensitive). */
	static bool IsKnownFormat(const FString& Format);

	/** The six accepted download formats (mirrored from the producer allow-list). */
	static const TArray<FString>& KnownFormats();

	//~ ----- "Generate Unreal formats" (on-demand materialize) -----

	/** POST/GET endpoint: request or poll an on-demand materialize for one order (orderId URL-encoded). */
	static FString BuildMaterializeUrl(const FString& BaseUrl, const FString& OrderId);

	/** True iff Scope is a materialize scope the plugin offers: "unreal" or "all" (case-insensitive). */
	static bool IsValidMaterializeScope(const FString& Scope);

	/**
	 * The explicit packaging-format tokens the Unreal importer consumes — the plugin's targeted
	 * layer set, and the single home for it. Sent to materialize instead of the server-side
	 * "unreal" keyword so the plugin, not the web tier, owns which layers a UE import needs
	 * (the keyword expands to only the four core tokens and would silently drop the vector/
	 * landcover layers this importer now reads).
	 */
	static const TArray<FString>& TargetedImportTokens();

	/**
	 * JSON request body for the materialize POST — the platform's vault contract is
	 * {"tokens": "unreal"|"all"|Token[]}. Scope "all" passes through as the keyword;
	 * "unreal" (and empty) sends the explicit TargetedImportTokens() array. Condensed.
	 */
	static FString BuildMaterializeBody(const FString& Scope);

	/**
	 * Parse the materialize POST response. A 2xx body carries {"jobId":..}; a 409 (single-flight)
	 * carries {"activeJobId":..} - both mean "a job is running", so both return true with OutJobId
	 * filled and bOutAlreadyRunning distinguishing them. Fails closed (false, fills OutError from the
	 * error body) when neither id is present.
	 */
	static bool ParseMaterializeStartResponse(const FString& JsonStr, FString& OutJobId, bool& bOutAlreadyRunning, FString& OutError);

	/** Map a materialize status string to the enum; unrecognized strings -> Unknown (not a parse error). */
	static EMantlePlaceMaterializeState ParseMaterializeState(const FString& State);

	/**
	 * Parse a materialize status body: {"status"|"state":.., "progress"|"fraction"?:.., "message"?:.., "jobId"?:..}.
	 * Returns true (filling OutStatus, including a Failed state) whenever a recognizable status field is
	 * present; a raw error envelope ({"error":..} with no status) fails closed (false, fills OutError).
	 * A progress value > 1 is treated as a percent and normalized to [0,1].
	 */
	static bool ParseMaterializeStatus(const FString& JsonStr, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError);

	//~ ----- Tier detection (drives the vault list surface) -----

	/**
	 * True iff the bundle lacks a UE-importable terrain mesh (no "glb" in its advertised formats) yet
	 * has some formats - i.e. a BASE bundle that needs "Generate Unreal formats". An item with no known
	 * formats (legacy/unknown) returns false, so the surface never nags about a bundle it can't classify.
	 */
	static bool IsIncompleteBundle(const FMantlePlaceVaultItem& Item);

	/** Short tier label for the list row: "Base" (needs materialize), "Unreal" (importable), or "Unknown". */
	static FString DeriveTierLabel(const FMantlePlaceVaultItem& Item);
};
