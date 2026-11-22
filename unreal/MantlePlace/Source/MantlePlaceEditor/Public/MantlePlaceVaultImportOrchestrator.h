// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "Containers/Ticker.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceAuthTypes.h"
#include "MantlePlaceClientLogic.h"
#include "MantlePlaceVaultImportOrchestrator.generated.h"

class UMantlePlaceAuthSystemBase;
class UMantlePlaceVaultClient;
class UMantlePlaceBundleCache;
class FMantlePlaceImportSteps;
class FMantlePlaceLevelChoice;

DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnImportStepsNative, TSharedRef<FMantlePlaceImportSteps>);

DECLARE_DELEGATE_RetVal_OneParam(bool, FMantlePlaceOnOfferLevelChoiceNative, TSharedRef<FMantlePlaceLevelChoice>);

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
 * (C++) completion delegates. It does NOT own the auth source: that is the editor's single session,
 * held by UMantlePlaceAuthSubsystem and resolved in EnsureClients. Initialize still accepts an
 * explicit auth source, for headless callers and tests that have no editor to ask.
 */
UCLASS(BlueprintType, Blueprintable)
class MANTLEPLACEEDITOR_API UMantlePlaceVaultImportOrchestrator : public UObject
{
	GENERATED_BODY()

public:
	// The three poll numbers below are project settings now (Project Settings -> Plugins -> Mantle
	// Place), not literals compiled into this class. They stay here as PER-ORCHESTRATOR OVERRIDES so
	// a Blueprint or a test that already set one keeps winning; zero means "unset — use the project
	// setting", which is what an untouched orchestrator now has. Read them through the three
	// Resolve* accessors below rather than directly: those are what apply the fallback.

	/** Seconds between materialize status polls. 0 = use the project setting. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	float MaterializePollIntervalSeconds = 0.0f;

	/** Give up after this many polls (interval x this ~= the materialize timeout). 0 = project setting. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	int32 MaterializeMaxPolls = 0;

	/** Tolerate this many consecutive failed status polls before failing. 0 = project setting.
	 *  A deliberate "fail on the first failure" is expressed by the project setting, not here —
	 *  this field cannot distinguish that from "unset", and the project setting can. */
	UPROPERTY(EditDefaultsOnly, BlueprintReadWrite, Category = "Mantle Place|Vault")
	int32 MaxConsecutivePollFailures = 0;

	float ResolvePollIntervalSeconds() const;
	int32 ResolveMaxPolls() const;
	int32 ResolveMaxConsecutivePollFailures() const;

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

	FMantlePlaceOnClientUpdateRequiredNative OnClientUpdateRequiredNative;

	FMantlePlaceOnImportStepsNative OnImportStepsNative;

	FMantlePlaceOnOfferLevelChoiceNative OnOfferLevelChoiceNative;

	/**
	 * Wire an explicit signed-in auth source (for a headless caller or a test that has no editor
	 * auth-state UI) whose JWT authorizes the vault calls, and construct the clients. Optional: if
	 * never called, the orchestrator takes the editor's one session from UMantlePlaceAuthSubsystem,
	 * or creates a private instance when there is no editor at all.
	 */
	/**
	 * Rebuild an auth session from the stored refresh token, if there is one.
	 *
	 * Called when the vault surface opens, NOT at editor startup: the vault browser is the only
	 * thing that needs a session, so an editor the user never points at the vault makes no auth
	 * request at all. Silently does nothing when no credential is stored.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	void TryRestoreSession();

	/** Why the last auth attempt failed, in words fit to show a user. Empty when nothing failed. */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	FString GetLastAuthError() const;

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
	 * Import a 3.0 order at the level the caller names (`Max`, `Med` or `Min`), for editor scripting (and
	 * unattended gates) that cannot press the import window's Download. It goes down the path that button
	 * takes: the same request naming Unreal's files, the same cache rule (the level the slot holds is
	 * imported again with no download, another level replaces it), the same steps, and the same OnImportPhase
	 * and OnImportFinished events. FMantlePlaceImportResult::Level says which level was downloaded.
	 *
	 * Refused before any request, returning false with the reason in an OnImportPhase "Error" (and in
	 * GetLevelImportRefusal): a level the order does not offer, any level on a 2.x order, and an empty level
	 * (StartVaultImport takes the platform's recommendation). Also false when an import is already running.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Vault")
	bool StartVaultImportAtLevel(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope, const FString& Level);

	/** Why StartVaultImportAtLevel would refuse Item at Level, in words; empty when it would not. */
	UFUNCTION(BlueprintPure, Category = "Mantle Place|Vault")
	static FString GetLevelImportRefusal(const FMantlePlaceVaultItem& Item, const FString& Level);

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

	/**
	 * Stop an in-flight import. While the bundle is fetched (generating, downloading), stops the polling
	 * and the download now and fires OnImportFinished(false). Once the import itself runs, the cancel
	 * takes effect between its steps: the running step finishes, the later ones do not start, and
	 * OnImportFinished fires when the import returns, with bCancelled set.
	 */
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

	UMantlePlaceVaultClient* GetVaultClient() const { return VaultClient; }

	virtual void BeginDestroy() override;

private:
	enum class EPhase : uint8
	{
		Idle,
		AutoPicking,
		ResolvingLocal,
		Materializing,
		ChoosingLevel,
		Polling,
		Relisting,
		Presigning,
		Downloading,
		Importing
	};

	void EnsureClients();

	void BeginItemImport();

	FString StageLocalZip(const FString& ZipPath);

	FString ComputeStagedSha256() const;

	void CleanupLocalStaging();

	void BeginPresign();

	bool BeginVaultImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope, const FString& Level);

	void BeginLevelChoice();

	void DownloadLevel(const FString& Level);

	void SchedulePoll();
	void DoPoll();
	void UnschedulePoll();

	void FinishImport(bool bSuccess, const FMantlePlaceImportResult& Result);
	void FailImport(const FString& Message);

	void EmitPhase(const FString& PhaseLabel, const FString& Message, float Fraction);

	void BeginSteps();

	FMantlePlaceImportResult RunBundleImport(const FString& Path);

	TSharedPtr<FMantlePlaceImportSteps> Steps;

	void EnsureAuthDelegateBound();

	void HandleAuthStateChangedNative(EMantlePlaceAuthState NewState);
	void HandleClientUpdateRequiredNative(const FMantlePlaceClientUpdate& Update);
	void HandleVaultListedNative(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	void HandleMaterializeStartedNative(bool bSuccess, const FMantlePlaceMaterializeStart& Start, const FString& Message);
	void HandleMaterializeStatusNative(bool bOk, const FMantlePlaceMaterializeStatus& Status, const FString& Message);
	void HandlePresignedNative(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message);
	void HandleDownloadProgressNative(const FMantlePlaceDownloadProgress& Progress);
	void HandleDownloadCompleteNative(bool bSuccess, const FString& LocalBundlePath, const FString& Message);

	UPROPERTY()
	TObjectPtr<UMantlePlaceAuthSystemBase> AuthSystem;

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

	TArray<FString> ActiveRequestedTokens;
	int32 PollCount = 0;
	int32 ConsecutivePollFailures = 0;
	FTSTicker::FDelegateHandle PollTicker;

	bool bMaterializeRunObserved = false;

	TArray<FString> LastOutstanding;

	int32 MaterializeRepicks = 0;
	static constexpr int32 MaxMaterializeRepicks = 1;

	bool bAttemptedMaterializeRecovery = false;

	bool bRouteStartPending = false;

	bool bViewRequested = false;
	bool bViewDeliveryKnown = false;

	TArray<FString> ViewDelivered;

	FString ViewManifestSha256;

	TArray<FString> LevelAbsentFiles;

	TSharedPtr<FMantlePlaceLevelChoice> LevelChoice;

	FString ScriptedLevel;

	bool bLocalImport = false;
	FString LocalOriginalPath;
	FString LocalStagedPath;
	FString LocalStagingDir;
};
