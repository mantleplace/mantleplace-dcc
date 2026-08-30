// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultImportOrchestrator.h"

#include "MantlePlaceAuthSystemBase.h"
#include "MantlePlaceVaultClient.h"
#include "MantlePlaceBundleCache.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest (local-zip inspection)
#include "MantlePlaceSha256.h"         // whole-bundle sha256 for the no-orderId local match

#include "HAL/PlatformFileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceVaultImport, Log, All);

void UMantlePlaceVaultImportOrchestrator::Initialize(UMantlePlaceAuthSystemBase* InAuthSystem)
{
	AuthSystem = InAuthSystem;
	EnsureClients();
}

void UMantlePlaceVaultImportOrchestrator::SignIn()
{
	EnsureClients();
	if (AuthSystem != nullptr)
	{
		AuthSystem->SignInWithBrowser();
	}
}

void UMantlePlaceVaultImportOrchestrator::SignOut()
{
	EnsureClients();
	if (AuthSystem != nullptr)
	{
		AuthSystem->SignOut(); // fires OnAuthStateChangedNative(Unauthenticated) -> HandleAuthStateChangedNative
	}
}

bool UMantlePlaceVaultImportOrchestrator::IsSignedIn() const
{
	return AuthSystem != nullptr && AuthSystem->IsAuthenticated();
}

EMantlePlaceAuthState UMantlePlaceVaultImportOrchestrator::GetAuthState() const
{
	return AuthSystem != nullptr ? AuthSystem->GetAuthState() : EMantlePlaceAuthState::Unauthenticated;
}

void UMantlePlaceVaultImportOrchestrator::EnsureAuthDelegateBound()
{
	if (AuthSystem == nullptr || BoundAuthSystem.Get() == AuthSystem)
	{
		return; // already bound to the current auth source
	}
	if (UMantlePlaceAuthSystemBase* Prev = BoundAuthSystem.Get())
	{
		Prev->OnAuthStateChangedNative.RemoveAll(this);
	}
	AuthSystem->OnAuthStateChangedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleAuthStateChangedNative);
	BoundAuthSystem = AuthSystem;
}

void UMantlePlaceVaultImportOrchestrator::HandleAuthStateChangedNative(EMantlePlaceAuthState NewState)
{
	OnAuthChanged.Broadcast(NewState);
}

void UMantlePlaceVaultImportOrchestrator::EnsureClients()
{
	if (AuthSystem == nullptr)
	{
		// No explicit auth injected: create the C++ base, which reads the DefaultGame.ini
		// [/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase] config from its CDO (WebLoginUrl,
		// TokenEndpointUrl, PlatformApiBaseUrl, SupabaseAnonKey, loopback ports).
		AuthSystem = NewObject<UMantlePlaceAuthSystemBase>(this);
	}

	if (VaultClient == nullptr)
	{
		VaultClient = NewObject<UMantlePlaceVaultClient>(this);
		VaultClient->OnVaultListedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleVaultListedNative);
		VaultClient->OnPresignedUrlReadyNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandlePresignedNative);
		VaultClient->OnMaterializeStartedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleMaterializeStartedNative);
		VaultClient->OnMaterializeStatusNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleMaterializeStatusNative);
	}
	// (Re)point the client at the current auth source - Initialize may be called again with a new one.
	VaultClient->Initialize(AuthSystem);

	if (BundleCache == nullptr)
	{
		BundleCache = NewObject<UMantlePlaceBundleCache>(this);
		BundleCache->OnDownloadCompleteNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleDownloadCompleteNative);
		BundleCache->OnDownloadProgressNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleDownloadProgressNative);
	}

	// Observe auth-state changes so the surface can auto-toggle Sign In/Out + refresh the list.
	EnsureAuthDelegateBound();
}

void UMantlePlaceVaultImportOrchestrator::RefreshVaultList()
{
	EnsureClients();
	VaultClient->ListVault(); // result -> HandleVaultListedNative -> OnVaultListed (EUW)
}

bool UMantlePlaceVaultImportOrchestrator::StartVaultImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope)
{
	if (IsBusy())
	{
		EmitPhase(TEXT("Busy"), TEXT("An import is already in progress."), -1.0f);
		return false;
	}
	if (Item.OrderId.IsEmpty())
	{
		EmitPhase(TEXT("Error"), TEXT("This bundle has no order id."), -1.0f);
		return false;
	}

	EnsureClients();

	// A vault-row import is not local: clear any prior local bookkeeping.
	bLocalImport = false;
	LocalOriginalPath.Reset();
	LocalStagedPath.Reset();
	LocalStagingDir.Reset();

	ActiveItem = Item;
	ActiveMode = Mode;
	ActiveScope = Scope.IsEmpty() ? TEXT("unreal") : Scope;
	ActiveJobId.Reset();
	ActiveRequestedTokens.Reset();
	PollCount = 0;
	ConsecutivePollFailures = 0;
	bAttemptedMaterializeRecovery = false;
	bMaterializeRunObserved = false;
	MaterializeRepicks = 0;
	LastOutstanding.Reset();

	BeginItemImport();
	return true;
}

void UMantlePlaceVaultImportOrchestrator::BeginItemImport()
{
	// Shared tail for both the vault-row path and the resolved local-zip path: an incomplete bundle
	// (no glb terrain mesh, incl. the empty-formats base_on_demand marker) materializes first; a bundle
	// that already ships Unreal formats downloads directly.
	if (UMantlePlaceVaultClient::IsBundleIncomplete(ActiveItem))
	{
		Phase = EPhase::Materializing;
		bAttemptedMaterializeRecovery = true;
		EmitPhase(TEXT("Generating"), FString::Printf(TEXT("Requesting Unreal formats (%s)..."), *ActiveScope), -1.0f);
		VaultClient->RequestMaterialize(ActiveItem.OrderId, ActiveScope);
	}
	else
	{
		// Already materialized (ships a glb terrain mesh) - the list item is fresh, so download directly.
		EmitPhase(TEXT("Downloading"), TEXT("Bundle already has Unreal formats - downloading..."), -1.0f);
		BeginPresign();
	}
}

bool UMantlePlaceVaultImportOrchestrator::StartVaultImportFirstIncomplete(EMantlePlaceImportMode Mode, const FString& Scope)
{
	if (IsBusy())
	{
		EmitPhase(TEXT("Busy"), TEXT("An import is already in progress."), -1.0f);
		return false;
	}

	EnsureClients();
	ActiveMode = Mode;
	ActiveScope = Scope.IsEmpty() ? TEXT("unreal") : Scope;
	Phase = EPhase::AutoPicking;
	EmitPhase(TEXT("Listing"), TEXT("Listing your vault to pick a bundle..."), -1.0f);
	VaultClient->ListVault(); // result -> HandleVaultListedNative (AutoPicking branch)
	return true;
}

bool UMantlePlaceVaultImportOrchestrator::StartLocalImport(const FString& ZipPath, EMantlePlaceImportMode Mode)
{
	if (IsBusy())
	{
		EmitPhase(TEXT("Busy"), TEXT("An import is already in progress."), -1.0f);
		return false;
	}
	if (ZipPath.IsEmpty())
	{
		EmitPhase(TEXT("Error"), TEXT("No bundle path provided."), -1.0f);
		return false;
	}

	EnsureClients();

	// Reset all flow bookkeeping for a local import.
	bLocalImport = true;
	LocalOriginalPath = ZipPath;
	LocalStagedPath.Reset();
	LocalStagingDir.Reset();
	ActiveItem = FMantlePlaceVaultItem();
	ActiveMode = Mode;
	ActiveScope = TEXT("unreal");
	ActiveJobId.Reset();
	ActiveRequestedTokens.Reset();
	PollCount = 0;
	ConsecutivePollFailures = 0;
	bAttemptedMaterializeRecovery = false;
	bMaterializeRunObserved = false;
	MaterializeRepicks = 0;
	LastOutstanding.Reset();

	// Stage: copy the user's zip into a private dir so their original file is never mutated (mirrors the
	// vault path, which downloads to the cache rather than importing in place).
	EmitPhase(TEXT("Staging"), TEXT("Copying the bundle to a staging folder..."), -1.0f);
	const FString Staged = StageLocalZip(ZipPath);
	if (Staged.IsEmpty())
	{
		FailImport(FString::Printf(TEXT("Could not stage the bundle for import: %s"), *ZipPath));
		return true; // request consumed - it started, then failed with a reported message
	}
	LocalStagedPath = Staged;

	// Inspect: does the staged bundle already ship its Unreal formats?
	FMantlePlaceVaultManifest Manifest;
	FString ReadError;
	if (!UMantlePlaceImporterLibrary::ReadVaultManifest(Staged, Manifest, ReadError))
	{
		FailImport(ReadError.IsEmpty() ? TEXT("Could not read the bundle manifest.") : ReadError);
		return true;
	}

	if (Manifest.bValid)
	{
		// Complete bundle -> import directly from the staged copy (no cloud round-trip).
		Phase = EPhase::Importing;
		EmitPhase(TEXT("Importing"), TEXT("Importing into the level..."), -1.0f);
		const FMantlePlaceImportResult Result = UMantlePlaceImporterLibrary::ImportVaultPackage(LocalStagedPath, ActiveMode);
		FinishImport(Result.bSuccess, Result);
		return true;
	}

	// Incomplete bundle (no `unreal` block) -> generate its Unreal formats on demand, then download +
	// import the completed bundle. Resolve the owning order first.
	if (!Manifest.OrderId.IsEmpty())
	{
		if (!IsSignedIn())
		{
			FailImport(TEXT("This bundle hasn't generated its Unreal formats yet. Sign in (top-right), then "
				"click Import again - the plugin will generate them in the cloud and import the completed bundle."));
			return true;
		}
		ActiveItem.OrderId = Manifest.OrderId;
		Phase = EPhase::Materializing;
		bAttemptedMaterializeRecovery = true;
		EmitPhase(TEXT("Generating"), TEXT("This bundle is missing Unreal content - generating it in the cloud..."), -1.0f);
		VaultClient->RequestMaterialize(ActiveItem.OrderId, ActiveScope);
		return true;
	}

	// No orderId in the manifest (a legacy / locally-produced bundle). Fall back to matching the zip to
	// an owned bundle by its whole-bundle sha256 - needs a signed-in vault to match against.
	if (!IsSignedIn())
	{
		FailImport(TEXT("This bundle hasn't generated its Unreal formats yet, and it predates the order tag "
			"needed to generate them automatically. Sign in and Import it from your vault list above instead."));
		return true;
	}
	Phase = EPhase::ResolvingLocal;
	EmitPhase(TEXT("Resolving"), TEXT("Matching this bundle to your vault..."), -1.0f);
	VaultClient->ListVault(); // result -> HandleVaultListedNative (ResolvingLocal branch)
	return true;
}

FString UMantlePlaceVaultImportOrchestrator::StageLocalZip(const FString& ZipPath)
{
	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		return FString();
	}

	const FString Guid = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	LocalStagingDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("LocalStaging") / Guid;
	if (!PlatformFile.CreateDirectoryTree(*LocalStagingDir))
	{
		LocalStagingDir.Reset();
		return FString();
	}

	const FString Dest = LocalStagingDir / FPaths::GetCleanFilename(ZipPath);
	if (!PlatformFile.CopyFile(*Dest, *ZipPath))
	{
		return FString();
	}
	return Dest;
}

FString UMantlePlaceVaultImportOrchestrator::ComputeStagedSha256() const
{
	if (LocalStagedPath.IsEmpty())
	{
		return FString();
	}
	TArray<uint8> Bytes;
	if (!FFileHelper::LoadFileToArray(Bytes, *LocalStagedPath))
	{
		return FString();
	}
	return MantlePlaceSha256::HexDigest(Bytes);
}

void UMantlePlaceVaultImportOrchestrator::CleanupLocalStaging()
{
	if (!LocalStagingDir.IsEmpty())
	{
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		PlatformFile.DeleteDirectoryRecursively(*LocalStagingDir);
	}
	bLocalImport = false;
	LocalOriginalPath.Reset();
	LocalStagedPath.Reset();
	LocalStagingDir.Reset();
}

void UMantlePlaceVaultImportOrchestrator::BeginPresign()
{
	Phase = EPhase::Presigning;
	// ⛔ Ask for the ARCHIVE by name. The old literal here was "glb", the platform's deprecated
	// whole-bundle alias, which returns the glb ARTIFACT whenever the order carries one and only
	// falls through to download.zip when it does not -- so this was correct by luck of the data.
	// The cache verifies against the listing's sha256, which is the archive's digest.
	VaultClient->GetPresignedBundleUrl(ActiveItem.OrderId); // -> HandlePresignedNative
}

void UMantlePlaceVaultImportOrchestrator::SchedulePoll()
{
	UnschedulePoll();

	if (PollCount >= MaterializeMaxPolls)
	{
		// Name the tokens. A bare "timed out" is the same sentence whether the platform was slow,
		// the run built the wrong set, or the layer can never be produced here — and the operator
		// reading it has no way to tell which, or what to check.
		FailImport(LastOutstanding.Num() == 0
		               ? TEXT("Timed out waiting for the Unreal formats to generate.")
		               : FString::Printf(
		                     TEXT("Timed out waiting for the Unreal formats to generate. Still missing: %s"),
		                     *FString::Join(LastOutstanding, TEXT(", "))));
		return;
	}

	const float Delay = FMath::Max(0.5f, MaterializePollIntervalSeconds);
	TWeakObjectPtr<UMantlePlaceVaultImportOrchestrator> WeakThis(this);
	PollTicker = FTSTicker::GetCoreTicker().AddTicker(TEXT("MantlePlaceMaterializePoll"), Delay,
		[WeakThis](float) -> bool
		{
			if (UMantlePlaceVaultImportOrchestrator* Self = WeakThis.Get())
			{
				Self->PollTicker.Reset(); // this one-shot has fired; clear our handle before re-arming
				Self->DoPoll();
			}
			return false; // one-shot; the next poll is armed only after a status result
		});
}

void UMantlePlaceVaultImportOrchestrator::DoPoll()
{
	if (Phase != EPhase::Polling)
	{
		return;
	}
	++PollCount;
	VaultClient->GetMaterializeStatus(ActiveItem.OrderId, ActiveRequestedTokens); // result -> HandleMaterializeStatusNative
}

void UMantlePlaceVaultImportOrchestrator::UnschedulePoll()
{
	if (PollTicker.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(PollTicker);
		PollTicker.Reset();
	}
}

void UMantlePlaceVaultImportOrchestrator::HandleMaterializeStartedNative(bool bSuccess, const FMantlePlaceMaterializeStart& Start, const FString& Message)
{
	if (Phase != EPhase::Materializing)
	{
		return; // stale completion from a cancelled/prior run
	}
	if (!bSuccess)
	{
		FailImport(Message.IsEmpty() ? TEXT("Could not start generating Unreal formats.") : Message);
		return;
	}

	// STOP: nothing to build means there is NO JOB, so there is nothing to poll. Polling anyway would
	// sit on "waiting for the platform to pick this up" for the whole budget and end in a timeout,
	// for a bundle that was ready before the request was made. Straight to the re-list, which is
	// where the post-materialize integrity facts come from either way.
	if (Start.Outcome == EMantlePlaceMaterializeStartOutcome::NothingToDo)
	{
		EmitPhase(TEXT("Generated"), TEXT("This bundle already has everything the importer needs."), 1.0f);
		Phase = EPhase::Relisting;
		VaultClient->ListVault();
		return;
	}

	ActiveJobId = Start.JobId;
	// The yardstick is what THIS HOST needs, and where that comes from depends on the outcome.
	//
	// A fresh run: Start.Tokens is the effective set the platform accepted for our own request, so
	// it is exactly right. Empty is legitimate (the body named none) and the client substitutes
	// this host's list, since the pure logic that owns it lives in the Runtime module's private
	// headers and is not visible here.
	//
	// ⛔ A JOINED run: Start.Tokens is THAT RUN's set, which is not this host's. The platform's
	// `tokens: 'unreal'` keyword and TargetedImportTokens() are two lists maintained in two
	// repositories, and they have already disagreed once — the run carried a layer this importer
	// does not use and omitted one it does. Adopting the run's set would make this import wait on
	// layers it does not need and stop waiting on one it does, which is a quieter version of the
	// same bug. So the host's own requirement stands, and the disagreement is settled after the run
	// ends by re-picking whatever it left outstanding (see HandleMaterializeStatusNative).
	if (Start.Outcome == EMantlePlaceMaterializeStartOutcome::Joined)
	{
		if (Start.Tokens.Num() > 0)
		{
			UE_LOG(LogMantlePlaceVaultImport, Log,
			       TEXT("Joined a run building %d deliverable(s): %s"),
			       Start.Tokens.Num(), *FString::Join(Start.Tokens, TEXT(", ")));
		}
		ActiveRequestedTokens.Reset();
	}
	else
	{
		ActiveRequestedTokens = Start.Tokens;
	}
	// A joined run was, by definition, already going — and may finish before the first poll lands,
	// so its activeJob is never seen. Without this seed that import waits for a job that has been
	// and gone.
	bMaterializeRunObserved = Start.Outcome == EMantlePlaceMaterializeStartOutcome::Joined;
	Phase = EPhase::Polling;
	PollCount = 0;
	ConsecutivePollFailures = 0;
	EmitPhase(TEXT("Generating"), Message, -1.0f);
	SchedulePoll();
}

void UMantlePlaceVaultImportOrchestrator::HandleMaterializeStatusNative(bool bOk, const FMantlePlaceMaterializeStatus& Status, const FString& Message)
{
	if (Phase != EPhase::Polling)
	{
		return;
	}

	if (!bOk)
	{
		if (++ConsecutivePollFailures > MaxConsecutivePollFailures)
		{
			FailImport(Message.IsEmpty() ? TEXT("Lost contact while generating Unreal formats.") : Message);
			return;
		}
		EmitPhase(TEXT("Generating"), TEXT("Status check failed - retrying..."), -1.0f);
		SchedulePoll();
		return;
	}

	ConsecutivePollFailures = 0;
	LastOutstanding = Status.Outstanding;

	switch (Status.State)
	{
	case EMantlePlaceMaterializeState::Complete:
		// A deliverable the platform will never produce for this area is a GAP, not a failure -
		// waiting for one is waiting forever. Say which, then carry on to the download.
		EmitPhase(TEXT("Generated"),
		          Status.Unproducible.Num() == 0
		              ? TEXT("Unreal formats are ready.")
		              : FString::Printf(TEXT("Unreal formats are ready. %d not available for this area."),
		                                Status.Unproducible.Num()),
		          1.0f);
		// Re-list to pick up the fresh (post-materialize) integrity facts - the download verifies the
		// bundle sha256 fail-closed, and the pre-materialize list item's sha is stale.
		Phase = EPhase::Relisting;
		VaultClient->ListVault();
		break;

	case EMantlePlaceMaterializeState::Failed:
		FailImport(Status.Message.IsEmpty() ? TEXT("Generating Unreal formats failed.") : Status.Message);
		break;

	case EMantlePlaceMaterializeState::Processing:
		bMaterializeRunObserved = true;
		EmitPhase(TEXT("Generating"),
			Status.Message.IsEmpty() ? TEXT("Generating Unreal formats...") : Status.Message,
			Status.Fraction);
		SchedulePoll();
		break;

	case EMantlePlaceMaterializeState::Pending:
		// ⛔ Pending with nothing in flight has TWO readings and the document cannot tell them apart:
		// the job row is not visible yet (wait — it will be), or the run we joined has ended without
		// building what we still need (waiting is forever). Polling through the second is a
		// ten-minute progress bar frozen at whatever fraction the joined run reached, ending in a
		// timeout that names nothing — 83%, with the platform finished the whole time (2026-08-30).
		//
		// Having SEEN a run is what separates them, and the answer to the second is to ask again:
		// the platform stops coalescing into a running row precisely because the client re-picks on
		// completion, and this host was the one that never did.
		if (bMaterializeRunObserved && Status.Outstanding.Num() > 0
		    && MaterializeRepicks < MaxMaterializeRepicks)
		{
			++MaterializeRepicks;
			UE_LOG(LogMantlePlaceVaultImport, Log,
			       TEXT("The materialize run ended with %d deliverable(s) still missing; asking for them: %s"),
			       Status.Outstanding.Num(), *FString::Join(Status.Outstanding, TEXT(", ")));
			EmitPhase(TEXT("Generating"),
			          FString::Printf(TEXT("That run did not include %d deliverable(s) - asking for them..."),
			                          Status.Outstanding.Num()),
			          Status.Fraction);
			UnschedulePoll();
			bMaterializeRunObserved = false;
			Phase = EPhase::Materializing;
			VaultClient->RequestMaterializeTokens(ActiveItem.OrderId, Status.Outstanding);
			break;
		}
		EmitPhase(TEXT("Generating"),
			Status.Message.IsEmpty() ? TEXT("Generating Unreal formats...") : Status.Message,
			Status.Fraction);
		SchedulePoll();
		break;

	case EMantlePlaceMaterializeState::Unknown:
	default:
		EmitPhase(TEXT("Generating"),
			Status.Message.IsEmpty() ? TEXT("Generating Unreal formats...") : Status.Message,
			Status.Fraction);
		SchedulePoll();
		break;
	}
}

void UMantlePlaceVaultImportOrchestrator::HandleVaultListedNative(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	// Always refresh the EUW list (user refresh, or the post-materialize re-list updating the tier badge).
	OnVaultListed.Broadcast(bSuccess, Bundles, Message);

	if (Phase == EPhase::AutoPicking)
	{
		Phase = EPhase::Idle; // clear so StartVaultImport's busy-guard lets the chain begin
		if (!bSuccess)
		{
			FailImport(FString::Printf(TEXT("Could not list the vault: %s"), *Message));
			return;
		}
		const FMantlePlaceVaultItem* Pick = Bundles.FindByPredicate(
			[](const FMantlePlaceVaultItem& Bundle) { return UMantlePlaceVaultClient::IsBundleIncomplete(Bundle); });
		if (Pick == nullptr && Bundles.Num() > 0)
		{
			Pick = &Bundles[0]; // none incomplete -> import the first (already materialized) bundle
		}
		if (Pick == nullptr)
		{
			FailImport(TEXT("Your vault has no bundles to import."));
			return;
		}
		StartVaultImport(*Pick, ActiveMode, ActiveScope);
		return;
	}

	if (Phase == EPhase::ResolvingLocal)
	{
		Phase = EPhase::Idle; // clear so BeginItemImport can (re)drive the flow from the matched item
		if (!bSuccess)
		{
			FailImport(FString::Printf(TEXT("Could not list your vault to match the bundle: %s"), *Message));
			return;
		}

		// Match the staged local zip to an owned bundle by its whole-bundle sha256 (the list's sha is the
		// same download.zip hash the bundle cache verifies against).
		const FString LocalSha = ComputeStagedSha256();
		const FMantlePlaceVaultItem* Match = LocalSha.IsEmpty()
			? nullptr
			: Bundles.FindByPredicate([&LocalSha](const FMantlePlaceVaultItem& Bundle)
				{ return Bundle.bHasSha256 && Bundle.Sha256.Equals(LocalSha, ESearchCase::IgnoreCase); });
		if (Match == nullptr)
		{
			FailImport(TEXT("Couldn't match this local bundle to one in your vault, so its Unreal formats "
				"can't be generated automatically. Import it from your vault list above instead."));
			return;
		}

		ActiveItem = *Match; // full item (formats/sha) so BeginItemImport + the download integrity check are correct
		BeginItemImport();
		return;
	}

	if (Phase != EPhase::Relisting)
	{
		return; // a plain user-initiated RefreshVaultList - nothing more to do
	}

	if (!bSuccess)
	{
		FailImport(FString::Printf(TEXT("Could not refresh the vault after generating: %s"), *Message));
		return;
	}

	const FMantlePlaceVaultItem* Fresh = Bundles.FindByPredicate(
		[this](const FMantlePlaceVaultItem& Bundle) { return Bundle.OrderId == ActiveItem.OrderId; });
	if (Fresh == nullptr)
	{
		FailImport(TEXT("The generated bundle did not appear in the vault list."));
		return;
	}

	ActiveItem = *Fresh;
	BeginPresign();
}

void UMantlePlaceVaultImportOrchestrator::HandlePresignedNative(bool bSuccess, const FMantlePlacePresignedDownload& Download, const FString& Message)
{
	if (Phase != EPhase::Presigning)
	{
		return;
	}
	if (!bSuccess)
	{
		FailImport(Message.IsEmpty() ? TEXT("Could not mint a download URL.") : Message);
		return;
	}

	Phase = EPhase::Downloading;
	EmitPhase(TEXT("Downloading"), TEXT("Downloading the bundle..."), 0.0f);
	BundleCache->DownloadBundle(ActiveItem, Download); // streamed to the local cache; result -> HandleDownloadCompleteNative
}

void UMantlePlaceVaultImportOrchestrator::HandleDownloadProgressNative(const FMantlePlaceDownloadProgress& Progress)
{
	if (Phase != EPhase::Downloading)
	{
		return;
	}
	EmitPhase(TEXT("Downloading"), FString(), Progress.Fraction);
}

void UMantlePlaceVaultImportOrchestrator::HandleDownloadCompleteNative(bool bSuccess, const FString& LocalBundlePath, const FString& Message)
{
	if (Phase != EPhase::Downloading)
	{
		return;
	}
	if (!bSuccess)
	{
		FailImport(Message.IsEmpty() ? TEXT("Download failed.") : Message);
		return;
	}

	// Pre-flight the manifest before announcing an import: the listing's completeness signal
	// decided the download, but the downloaded bytes are the authority — the listing has advertised
	// completeness a base bundle did not have. A bundle that arrived without its Unreal payload is
	// completed in the cloud (the recovery the local-zip path has always run) rather than failing
	// closed on the importer's manifest gate; the re-listed sha then invalidates this cached zip.
	FMantlePlaceVaultManifest PreflightManifest;
	FString PreflightError;
	const bool bManifestReadable =
		UMantlePlaceImporterLibrary::ReadVaultManifest(LocalBundlePath, PreflightManifest, PreflightError);
	const FString RecoveryOrderId =
		ActiveItem.OrderId.IsEmpty() ? PreflightManifest.OrderId : ActiveItem.OrderId;
	if (UMantlePlaceVaultClient::ShouldRecoverMissingUnrealPayload(
			bManifestReadable, PreflightManifest.bValid, RecoveryOrderId, bAttemptedMaterializeRecovery))
	{
		bAttemptedMaterializeRecovery = true;
		ActiveItem.OrderId = RecoveryOrderId;
		Phase = EPhase::Materializing;
		EmitPhase(TEXT("Generating"),
			TEXT("The downloaded bundle is missing Unreal content - generating it in the cloud..."), -1.0f);
		VaultClient->RequestMaterialize(ActiveItem.OrderId, ActiveScope);
		return;
	}

	Phase = EPhase::Importing;
	EmitPhase(TEXT("Importing"), TEXT("Importing into the level..."), -1.0f);

	// ImportVaultPackage is synchronous and enforces its own fail-closed manifest sha256 checks. The
	// freshly downloaded, materialized bundle is imported here - for a local import that means the
	// completed cloud bundle, not the user's incomplete local copy (which is left untouched on disk).
	FMantlePlaceImportResult Result = UMantlePlaceImporterLibrary::ImportVaultPackage(LocalBundlePath, ActiveMode);
	if (bLocalImport && Result.bSuccess)
	{
		Result.Message = FString::Printf(
			TEXT("Your local bundle was missing Unreal content, so a complete bundle was generated in the "
				"cloud and imported. Your original file (%s) is unchanged.\n%s"),
			*LocalOriginalPath, *Result.Message);
	}
	FinishImport(Result.bSuccess, Result);
}

void UMantlePlaceVaultImportOrchestrator::FinishImport(bool bSuccess, const FMantlePlaceImportResult& Result)
{
	UnschedulePoll();
	Phase = EPhase::Idle;
	UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("Vault import %s: %s"),
		bSuccess ? TEXT("succeeded") : TEXT("failed"), *Result.Message);
	OnImportFinished.Broadcast(bSuccess, Result);
	// Drop the local staging copy (no-op for a vault-row import) once the terminal result is out.
	CleanupLocalStaging();
}

void UMantlePlaceVaultImportOrchestrator::FailImport(const FString& Message)
{
	FMantlePlaceImportResult Result;
	Result.bSuccess = false;
	Result.Message = Message;
	FinishImport(false, Result);
}

void UMantlePlaceVaultImportOrchestrator::EmitPhase(const FString& PhaseLabel, const FString& Message, float Fraction)
{
	// Message-less emits are per-callback download progress ticks — dozens per
	// second (measured: ~50 lines/s flooding the log through a 78 MB pull).
	// The UI progress bar still gets every tick via the broadcast below; the
	// LOG narrates transitions, which all carry a message.
	if (!Message.IsEmpty())
	{
		if (Fraction >= 0.0f)
		{
			UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("[%s] %s (%.0f%%)"), *PhaseLabel, *Message, Fraction * 100.0f);
		}
		else
		{
			UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("[%s] %s"), *PhaseLabel, *Message);
		}
	}
	OnImportPhase.Broadcast(PhaseLabel, Message, Fraction);
}

void UMantlePlaceVaultImportOrchestrator::CancelImport()
{
	if (Phase == EPhase::Idle)
	{
		return;
	}
	UnschedulePoll();
	if (BundleCache != nullptr)
	{
		BundleCache->CancelDownload();
	}
	FMantlePlaceImportResult Result;
	Result.bSuccess = false;
	Result.Message = TEXT("Import cancelled.");
	FinishImport(false, Result);
}

bool UMantlePlaceVaultImportOrchestrator::IsBundleIncomplete(const FMantlePlaceVaultItem& Item)
{
	return UMantlePlaceVaultClient::IsBundleIncomplete(Item);
}

FString UMantlePlaceVaultImportOrchestrator::GetBundleTierLabel(const FMantlePlaceVaultItem& Item)
{
	return UMantlePlaceVaultClient::GetBundleTierLabel(Item);
}

void UMantlePlaceVaultImportOrchestrator::BeginDestroy()
{
	UnschedulePoll();
	Super::BeginDestroy();
}
