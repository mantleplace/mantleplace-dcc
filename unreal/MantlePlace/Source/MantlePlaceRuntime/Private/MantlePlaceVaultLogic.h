// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.h"

// Forward-declared rather than included: only TSharedPtr<FJsonObject> appears in a signature here,
// and the editor target resolving it transitively is what let the game-target break ship unseen.
class FJsonObject;

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

	/**
	 * The format token that names the packaged archive rather than one artifact inside it.
	 *
	 * The DEPRECATED alias for this is the literal "glb", which this plugin used to send. It is
	 * genuinely ambiguous: "glb" is also a real artifact format, so the platform looks up a glb
	 * artifact FIRST and only falls through to the whole zip when the order has none. An order that
	 * does carry one answers with that mesh -- and the bundle cache then verifies it against the
	 * listing's sha256, which is the ARCHIVE's digest, so a download that succeeded fails integrity.
	 * We were never getting the zip on purpose; we were getting it when the data happened to allow.
	 */
	static const FString& WholeBundleFormat();

	/** True iff the presign route accepts Format: any artifact format, plus the whole-bundle token. */
	static bool IsPresignableFormat(const FString& Format);

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
	 * Parse the materialize POST response - all five shapes the platform sends.
	 *
	 * STOP: each outcome is keyed on ITS OWN MARKER, never on the absence of `jobId`. That inference
	 * is what broke this: {"noop":true,..} and {"queued":true,..} are SUCCESSES that name no job, and
	 * both read as "the platform accepted the request but named no job to poll".
	 *
	 *   201 {"jobId",..}                     -> Started
	 *   200 {"coalesced":true,"activeJobId"} -> Joined  (note: NO `jobId` in this one)
	 *   200 {"noop":true,"delivered"}        -> NothingToDo
	 *   202 {"queued":true,"pendingTokens"}  -> Queued
	 *   409 {"error","code":"active_job",..} -> Joined
	 *
	 * A join with no job id is STILL a join: the 409's `activeJobId` may be null, and polling is keyed
	 * on the ORDER, not the job (the status GET never took a job id), so an unnamed run is fully
	 * followable. Refusing here tells a curator to retry a build that is already going.
	 *
	 * Fails closed (false, fills OutError from the error body) only for a genuine error envelope or a
	 * body that is none of the five.
	 */
	static bool ParseMaterializeStartResponse(const FString& JsonStr, FMantlePlaceMaterializeStart& OutStart, FString& OutError);

	/**
	 * The four-argument shape, kept so the corpus drivers that assert jobId/alreadyRunning keep
	 * working unchanged. New callers take the outcome.
	 */
	static bool ParseMaterializeStartResponse(const FString& JsonStr, FString& OutJobId, bool& bOutAlreadyRunning, FString& OutError);

	/** True for a `not_delivered` reason that no amount of waiting or retrying will change. */
	static bool IsPermanentlyAbsentReason(const FString& Reason);

	/**
	 * The token set to measure delivery against while polling.
	 *
	 * The start response echoes the EFFECTIVE set - already deduped against what is delivered and
	 * against what can never be produced here - so it beats this host's own list, and is the only
	 * correct answer for the "all" scope whose expansion lives on the server. Falls back to
	 * TargetedImportTokens() when the body named none.
	 */
	static TArray<FString> RequestedForPolling(const FMantlePlaceMaterializeStart& Start);

	/** Map a materialize status string to the enum; unrecognized strings -> Unknown (not a parse error). */
	static EMantlePlaceMaterializeState ParseMaterializeState(const FString& State);

	/**
	 * Parse a materialize status body. TWO shapes.
	 *
	 * A body carrying {"status"|"state":..} is a job-status document and is read as one:
	 * {"progress"|"fraction"?:.., "message"?:.., "jobId"?:..}, a value > 1 treated as a percent and
	 * normalized to [0,1].
	 *
	 * Otherwise the platform answers this endpoint with a DELIVERY-STATE document -
	 * {"delivered":[..],"notDelivered":[{token,reason}],"activeJob":{..}|null,"lastAttempt":..} -
	 * which carries no status word ANYWHERE, so completion is derived from `delivered` against
	 * `Requested`. That derivation is the more truthful reading either way: a run reports `completed`
	 * even when every token's emit failed, because per-token errors are swallowed by a soft-fail
	 * envelope. Delivery is proof of production; a job status is not.
	 *
	 * A raw error envelope with neither shape fails closed (false, fills OutError).
	 */
	static bool ParseMaterializeStatus(const FString& JsonStr, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError);

	/** The job-status shape only - no requested set, so nothing to derive delivery against. */
	static bool ParseMaterializeStatus(const FString& JsonStr, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError);

	/**
	 * Derive a state from the platform's delivery-state document.
	 *
	 * The row order IS the design, and three rows are traps:
	 *
	 *  - STOP: unreadable delivery state is checked first. `deliveryStateUnknown` means `delivered`
	 *    is empty for want of an answer, not because the bundle is empty. Falling through with an
	 *    empty requested set would compute "nothing outstanding" and report Complete - handing over a
	 *    bundle the platform never confirmed. Unknown is not terminal, so polling continues.
	 *  - STOP: nothing outstanding beats a running job. If everything asked for is on hand, an
	 *    in-flight job is building someone else's pick, and waiting stalls a possible download.
	 *  - STOP: a terminal attempt that left tokens outstanding is a failure whatever it called
	 *    itself. The verdict keys on the TOKENS; `outcome` only picks the sentence. Reading
	 *    `completed` as success is the silent loop where a curator regenerates forever.
	 *
	 * The last row is Pending, never Complete: that is the normal state in the seconds after a start,
	 * before the job row is visible. `activeJob.steps` is deliberately NOT read for progress - it is
	 * an unvalidated jsonb ladder the platform owns.
	 */
	static void DeriveMaterializeDelivery(const TSharedPtr<FJsonObject>& Root, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus);

	//~ ----- Tier detection (drives the vault list surface) -----

	/**
	 * True iff the bundle lacks a UE-importable terrain mesh (no "glb" in its advertised formats) yet
	 * has some formats - i.e. a BASE bundle that needs "Generate Unreal formats". An item with no known
	 * formats (legacy/unknown) returns false, so the surface never nags about a bundle it can't classify.
	 */
	static bool IsIncompleteBundle(const FMantlePlaceVaultItem& Item);

	/** Short tier label for the list row: "Base" (needs materialize), "Unreal" (importable), or "Unknown". */
	static FString DeriveTierLabel(const FMantlePlaceVaultItem& Item);

	//~ ----- Post-download verdict (the vault path's self-heal) -----

	/**
	 * True iff a downloaded bundle whose manifest was read but found incomplete should be completed
	 * in the cloud (materialize -> re-list -> re-download) instead of being handed to the importer,
	 * which would fail closed on its manifest gate. The listing's completeness signal has been
	 * wrong before (a whole-bundle alias advertised as `glb` read as a terrain mesh); this verdict
	 * makes the downloaded bytes, not the listing, the authority. One recovery per run: with
	 * bAlreadyRecovered the zip goes to the importer, whose gate reports the manifest's own
	 * readiness guidance instead of looping.
	 */
	static bool ShouldRecoverMissingUnrealPayload(
		bool bManifestReadable, bool bManifestValid, const FString& OrderId, bool bAlreadyRecovered);
};
