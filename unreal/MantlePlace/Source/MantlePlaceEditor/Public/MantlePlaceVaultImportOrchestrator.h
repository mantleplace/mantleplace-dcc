// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "Containers/Ticker.h"
#include "MantlePlaceVaultTypes.h"       // runtime: FMantlePlaceVaultItem / FMantlePlaceMaterializeStatus / FMantlePlacePresignedDownload
#include "MantlePlaceBundleCacheTypes.h" // runtime: FMantlePlaceDownloadProgress
#include "MantlePlaceImportTypes.h"      // editor:  EMantlePlaceImportMode / FMantlePlaceImportResult
#include "MantlePlaceAuthTypes.h"        // runtime: EMantlePlaceAuthState (auth-changed relay)
#include "MantlePlaceVaultImportOrchestrator.generated.h"

class UMantlePlaceAuthSystemBase;
class UMantlePlaceVaultClient;
class UMantlePlaceBundleCache;

/** Blueprint-assignable events for the EUW surface (a WidgetBlueprint binds these in its graph). */
DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnVaultListedBP, bool, bSuccess, const TArray<FMantlePlaceVaultItem>&, Bundles, const FString&, Message);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnVaultImportPhaseBP, const FString&, Phase, const FString&, Message, float, Fraction);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FMantlePlaceOnVaultImportFinishedBP, bool, bSuccess, const FMantlePlaceImportResult&, Result);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnAuthChangedBP, EMantlePlaceAuthState, NewState);

/**
 * Editor-side facade that turns the incomplete-bundle dead-end into a one-button flow:
 * materialize ("Generate Unreal formats") -> poll -> re-list for fresh integrity facts ->
 * mint a presigned download -> stream to the local cache -> ImportVaultPackage. The whole
 * async chain lives here in C++ (reviewable / unit-testable at the FMantlePlaceVaultLogic layer);
 * the Editor Utility Widget is surface only - it calls RefreshVaultList / StartVaultImport and
 * binds the three Blueprint-assignable events below.
 *
 * Owns a UMantlePlaceVaultClient + UMantlePlaceBundleCache and observes them through their native
 * (C++) completion delegates. The auth source (a BP_MantlePlaceAuthSystemBase instance the EUW
 * signs in) is injected via Initialize and supplies the JWT for the vault calls.
 */
UCLASS(BlueprintType, Blueprintable)
class MANTLEPLACEEDITOR_API UMantlePlaceVaultImportOrchestrator : public UObject
{
	GENERATED_BODY()

public:
	/** Seconds between materialize status polls. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	float MaterializePollIntervalSeconds = 3.0f;

	/** Give up after this many polls (interval x this ~= the materialize timeout). */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	int32 MaterializeMaxPolls = 200;

	/** Tolerate this many consecutive failed status polls (transient network) before failing. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	int32 MaxConsecutivePollFailures = 5;

	/** Fired when a vault list finishes (user refresh or the internal post-materialize re-list). */
	UPROPERTY(BlueprintAssignable, Category = "Mantle Place|Vault")
	FMantlePlaceOnVaultListedBP OnVaultListed;

	/** Fired on each import-flow phase change / progress tick (Fraction is -1 when indeterminate). */
	UPROPERTY(BlueprintAssignable, Category = "Mantle Place|Vault")
	FMantlePlaceOnVaultImportPhaseBP OnImportPhase;

	/** Fired once when the whole import flow terminates (success or failure; Result carries the detail). */
	UPROPERTY(BlueprintAssignable, Category = "Mantle Place|Vault")
	FMantlePlaceOnVaultImportFinishedBP OnImportFinished;

	/**
	 * Fired on every auth-state change from the auth source (relayed from its native delegate), so the
	 * surface can toggle Sign In / Sign Out and auto-list on sign-in / clear on sign-out.
	 */
	UPROPERTY(BlueprintAssignable, Category = "Mantle Place|Vault")
	FMantlePlaceOnAuthChangedBP OnAuthChanged;

	/**
	 * Wire an explicit signed-in auth source (e.g. a BP_MantlePlaceAuthSystemBase instance for richer
	 * auth-state UI) whose JWT authorizes the vault calls, and construct the clients. Optional: if never
	 * called, the orchestrator lazily creates a plain UMantlePlaceAuthSystemBase (which still reads the
	 * DefaultGame.ini auth config from its CDO) on first use.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void Initialize(UMantlePlaceAuthSystemBase* InAuthSystem);

	/** Begin the browser (PKCE) sign-in on the auth source. Lazily creates the auth source if needed. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void SignIn();

	/** Sign out of the auth source (clears the cached local session). Auto-refresh fires via OnAuthChanged. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void SignOut();

	/** True when the auth source holds a valid, non-expired session (drives the surface's signed-in state). */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	bool IsSignedIn() const;

	/** Current auth-source state (Unauthenticated / Authenticating / Authenticated / ...) for the button label. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	EMantlePlaceAuthState GetAuthState() const;

	/** List the signed-in curator's owned bundles. Result via OnVaultListed. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void RefreshVaultList();

	/**
	 * Vault-first import. If Item is incomplete (a BASE bundle) it is materialized with Scope
	 * ("unreal" | "all"), re-listed for its fresh integrity facts, then downloaded + imported; a
	 * bundle that already ships Unreal formats skips straight to download + import. Progress via
	 * OnImportPhase, the terminal result via OnImportFinished. Returns false (and starts nothing) if
	 * an import is already running or the inputs are invalid.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	bool StartVaultImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope);

	/**
	 * One-click convenience for the EUW: list the vault, auto-pick the first incomplete (BASE) bundle
	 * (or the first bundle if none are incomplete), and run StartVaultImport on it. Keeps the whole
	 * pick-and-import decision in C++ so the surface is a single button. Returns false if already busy.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	bool StartVaultImportFirstIncomplete(EMantlePlaceImportMode Mode, const FString& Scope);

	/**
	 * Local-zip import that mirrors the vault path so "Import" always works. The user points at a
	 * downloaded bundle .zip; it is copied into a private staging dir (their original file is never
	 * modified) and inspected. If it already ships its Unreal formats it imports directly from the
	 * staged copy. If it is missing them (a base_on_demand bundle) the owning order is resolved from the
	 * manifest's orderId - or, failing that, by matching the zip's whole-bundle sha256 against the
	 * signed-in vault - its Unreal formats are generated on demand, the completed bundle is downloaded,
	 * and THAT is imported instead (with a notice that the local file was incomplete). Progress via
	 * OnImportPhase, the result via OnImportFinished. Sign-in is required only when a materialize is
	 * actually needed. Returns false (starts nothing) if already busy or the path is empty.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	bool StartLocalImport(const FString& ZipPath, EMantlePlaceImportMode Mode);

	/** Abort an in-flight import (stops polling + any download). Fires OnImportFinished(false). */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void CancelImport();

	/** True while an import flow is running. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	bool IsBusy() const { return Phase != EPhase::Idle; }

	/** True iff Item is an incomplete (BASE) bundle needing "Generate Unreal formats" (list-row helper). */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	static bool IsBundleIncomplete(const FMantlePlaceVaultItem& Item);

	/** Short tier label for a vault list row: "Base", "Unreal", or "Unknown". */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	static FString GetBundleTierLabel(const FMantlePlaceVaultItem& Item);

	//~ Begin UObject interface
	virtual void BeginDestroy() override;
	//~ End UObject interface

private:
	/** Where we are in the async chain (guards stale async completions from a prior/cancelled run). */
	enum class EPhase : uint8
	{
		Idle,
		AutoPicking,    // ListVault in flight for StartVaultImportFirstIncomplete
		ResolvingLocal, // ListVault in flight to sha256-match a local zip with no manifest orderId
		Materializing,  // RequestMaterialize in flight
		Polling,        // GetMaterializeStatus loop
		Relisting,      // re-list to obtain the fresh (post-materialize) item
		Presigning,     // GetPresignedUrl in flight
		Downloading,    // streamed download in flight
		Importing       // ImportVaultPackage running (synchronous)
	};

	/** Lazily construct + wire the vault client and bundle cache (idempotent). */
	void EnsureClients();

	/** Shared tail of the vault + resolved-local paths: materialize (incomplete) or download+import (ready). */
	void BeginItemImport();

	/** Copy ZipPath into a fresh private staging dir (LocalStagingDir); returns the staged path, empty on failure. */
	FString StageLocalZip(const FString& ZipPath);

	/** Whole-bundle sha256 (lowercase hex) of the staged zip, or empty if it can't be read. */
	FString ComputeStagedSha256() const;

	/** Best-effort delete of LocalStagingDir + reset the local-import bookkeeping (called from FinishImport). */
	void CleanupLocalStaging();

	/** Mint a presigned "glb" URL for ActiveItem, then download it. */
	void BeginPresign();

	/** Arm a one-shot status-poll timer (respecting MaterializeMaxPolls). */
	void SchedulePoll();
	void DoPoll();
	void UnschedulePoll();

	/** Terminal transitions. */
	void FinishImport(bool bSuccess, const FMantlePlaceImportResult& Result);
	void FailImport(const FString& Message);

	/** Broadcast an OnImportPhase update (and log it). */
	void EmitPhase(const FString& PhaseLabel, const FString& Message, float Fraction);

	/** Bind (once) the auth source's native auth-changed delegate, re-broadcasting it as OnAuthChanged. */
	void EnsureAuthDelegateBound();

	//~ Native-delegate handlers bound on the vault client / bundle cache / auth source.
	void HandleAuthStateChangedNative(EMantlePlaceAuthState NewState);
	void HandleVaultListedNative(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void HandleMaterializeStartedNative(bool bSuccess, const FString& JobId, const FString& Message);
	void HandleMaterializeStatusNative(bool bOk, const FMantlePlaceMaterializeStatus& Status, const FString& Message);
	void HandlePresignedNative(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message);
	void HandleDownloadProgressNative(const FMantlePlaceDownloadProgress& Progress);
	void HandleDownloadCompleteNative(bool bSuccess, const FString& LocalBundlePath, const FString& Message);

	UPROPERTY()
	TObjectPtr<UMantlePlaceAuthSystemBase> AuthSystem;

	/** The auth source we last bound OnAuthStateChangedNative on (so a re-Initialize can rebind cleanly). */
	TWeakObjectPtr<UMantlePlaceAuthSystemBase> BoundAuthSystem;

	UPROPERTY()
	TObjectPtr<UMantlePlaceVaultClient> VaultClient;

	UPROPERTY()
	TObjectPtr<UMantlePlaceBundleCache> BundleCache;

	EPhase Phase = EPhase::Idle;
	FMantlePlaceVaultItem ActiveItem;
	EMantlePlaceImportMode ActiveMode = EMantlePlaceImportMode::Landscape;
	FString ActiveScope;
	FString ActiveJobId;
	int32 PollCount = 0;
	int32 ConsecutivePollFailures = 0;
	FTSTicker::FDelegateHandle PollTicker;

	//~ Local-zip import bookkeeping (all empty/false for a vault-row import).
	bool bLocalImport = false;    // this flow originated from a local .zip
	FString LocalOriginalPath;    // the user's zip path (never modified) - referenced in the "unchanged" notice
	FString LocalStagedPath;      // the staged copy (imported directly when the local bundle is already complete)
	FString LocalStagingDir;      // staging dir to delete when the flow ends
};
