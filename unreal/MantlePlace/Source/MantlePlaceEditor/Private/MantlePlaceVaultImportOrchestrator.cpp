// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceVaultImportOrchestrator.h"

#include "MantlePlaceAuthSubsystem.h"
#include "MantlePlaceAuthSystemBase.h"
#include "MantlePlaceVaultClient.h"
#include "MantlePlaceBundleCache.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceLevelChoiceLogic.h"
#include "MantlePlaceManifestThreeLogic.h"
#include "MantlePlaceScriptedLevelLogic.h"
#include "MantlePlaceSha256.h"

#include "HAL/PlatformFileManager.h"
#include "HAL/PlatformTime.h"
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
		AuthSystem->SignOut();
	}
}

void UMantlePlaceVaultImportOrchestrator::TryRestoreSession()
{
	EnsureClients();
	if (AuthSystem != nullptr)
	{
		AuthSystem->TryRestoreSession();
	}
}

FString UMantlePlaceVaultImportOrchestrator::GetLastAuthError() const
{
	return AuthSystem != nullptr ? AuthSystem->GetLastAuthError() : FString();
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
		return;
	}
	if (UMantlePlaceAuthSystemBase* Prev = BoundAuthSystem.Get())
	{
		Prev->OnAuthStateChangedNative.RemoveAll(this);
		Prev->OnClientUpdateRequiredNative.RemoveAll(this);
	}
	AuthSystem->OnAuthStateChangedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleAuthStateChangedNative);
	AuthSystem->OnClientUpdateRequiredNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleClientUpdateRequiredNative);
	BoundAuthSystem = AuthSystem;
}

void UMantlePlaceVaultImportOrchestrator::HandleAuthStateChangedNative(EMantlePlaceAuthState NewState)
{
	OnAuthChanged.Broadcast(NewState);
}

void UMantlePlaceVaultImportOrchestrator::HandleClientUpdateRequiredNative(const FMantlePlaceClientUpdate& Update)
{
	OnClientUpdateRequiredNative.Broadcast(Update);
}

void UMantlePlaceVaultImportOrchestrator::EnsureClients()
{
	if (AuthSystem == nullptr)
	{
		if (UMantlePlaceAuthSubsystem* AuthSubsystem = UMantlePlaceAuthSubsystem::Get())
		{
			AuthSystem = AuthSubsystem->GetAuthSystem();
		}
	}
	if (AuthSystem == nullptr)
	{
		AuthSystem = NewObject<UMantlePlaceAuthSystemBase>(this);
	}

	if (VaultClient == nullptr)
	{
		VaultClient = NewObject<UMantlePlaceVaultClient>(this);
		VaultClient->OnVaultListedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleVaultListedNative);
		VaultClient->OnClientUpdateRequiredNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleClientUpdateRequiredNative);
		VaultClient->OnPresignedUrlReadyNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandlePresignedNative);
		VaultClient->OnMaterializeStartedNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleMaterializeStartedNative);
		VaultClient->OnMaterializeStatusNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleMaterializeStatusNative);
	}
	VaultClient->Initialize(AuthSystem);

	if (BundleCache == nullptr)
	{
		BundleCache = NewObject<UMantlePlaceBundleCache>(this);
		BundleCache->OnDownloadCompleteNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleDownloadCompleteNative);
		BundleCache->OnDownloadProgressNative.AddUObject(this, &UMantlePlaceVaultImportOrchestrator::HandleDownloadProgressNative);
	}

	EnsureAuthDelegateBound();
}

void UMantlePlaceVaultImportOrchestrator::RefreshVaultList()
{
	EnsureClients();
	VaultClient->ListVault();
}

bool UMantlePlaceVaultImportOrchestrator::StartVaultImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope)
{
	return BeginVaultImport(Item, Mode, Scope, FString());
}

FString UMantlePlaceVaultImportOrchestrator::GetLevelImportRefusal(const FMantlePlaceVaultItem& Item, const FString& Level)
{
	return FMantlePlaceScriptedLevelLogic::Refusal(Item, Level);
}

bool UMantlePlaceVaultImportOrchestrator::StartVaultImportAtLevel(
	const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope, const FString& Level)
{
	if (IsBusy())
	{
		EmitPhase(TEXT("Busy"), TEXT("An import is already in progress."), -1.0f);
		return false;
	}
	const FString Refusal = FMantlePlaceScriptedLevelLogic::Refusal(Item, Level);
	if (!Refusal.IsEmpty())
	{
		EmitPhase(TEXT("Error"), Refusal, -1.0f);
		return false;
	}
	return BeginVaultImport(Item, Mode, Scope, Level);
}

bool UMantlePlaceVaultImportOrchestrator::BeginVaultImport(
	const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode, const FString& Scope, const FString& Level)
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
	bRouteStartPending = false;
	bViewRequested = false;
	bViewDeliveryKnown = false;
	ViewDelivered.Reset();
	ViewManifestSha256.Reset();
	LevelChoice.Reset();
	ScriptedLevel = Level;
	bMaterializeRunObserved = false;
	MaterializeRepicks = 0;
	LastOutstanding.Reset();

	BeginSteps();
	BeginItemImport();
	return true;
}

void UMantlePlaceVaultImportOrchestrator::BeginItemImport()
{
	if (Steps.IsValid())
	{
		const FName DownloadStep(TEXT("download"));
		Steps->Add(DownloadStep, TEXT("Download"));
		Steps->Begin(DownloadStep, FPlatformTime::Seconds());
	}
	if (!bLocalImport && ActiveItem.bHasLevels)
	{
		if (!ScriptedLevel.IsEmpty())
		{
			bAttemptedMaterializeRecovery = true;
			DownloadLevel(ScriptedLevel);
			return;
		}
		BeginLevelChoice();
		return;
	}
	if (UMantlePlaceVaultClient::IsBundleIncomplete(ActiveItem))
	{
		Phase = EPhase::Materializing;
		bAttemptedMaterializeRecovery = true;
		bRouteStartPending = !bLocalImport;
		EmitPhase(TEXT("Generating"), FString::Printf(TEXT("Requesting Unreal formats (%s)..."), *ActiveScope), -1.0f);
		VaultClient->RequestMaterialize(ActiveItem.OrderId, ActiveScope);
	}
	else
	{
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
	BeginSteps();
	EmitPhase(TEXT("Listing"), TEXT("Listing your vault to pick a bundle..."), -1.0f);
	VaultClient->ListVault();
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
	bRouteStartPending = false;
	bViewRequested = false;
	bViewDeliveryKnown = false;
	ViewDelivered.Reset();
	ViewManifestSha256.Reset();
	bMaterializeRunObserved = false;
	MaterializeRepicks = 0;
	LastOutstanding.Reset();

	BeginSteps();

	EmitPhase(TEXT("Staging"), TEXT("Copying the bundle to a staging folder..."), -1.0f);
	const FString Staged = StageLocalZip(ZipPath);
	if (Staged.IsEmpty())
	{
		FailImport(FString::Printf(TEXT("Could not stage the bundle for import: %s"), *ZipPath));
		return true;
	}
	LocalStagedPath = Staged;

	FMantlePlaceVaultManifest Manifest;
	FString ReadError;
	if (!UMantlePlaceImporterLibrary::ReadVaultManifest(Staged, Manifest, ReadError))
	{
		FailImport(ReadError.IsEmpty() ? TEXT("Could not read the bundle manifest.") : ReadError);
		return true;
	}

	if (Manifest.bValid)
	{
		Phase = EPhase::Importing;
		EmitPhase(TEXT("Importing"), TEXT("Importing into the level..."), -1.0f);
		const FMantlePlaceImportResult Result = RunBundleImport(LocalStagedPath);
		FinishImport(Result.bSuccess, Result);
		return true;
	}

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

	if (!IsSignedIn())
	{
		FailImport(TEXT("This bundle hasn't generated its Unreal formats yet, and it predates the order tag "
			"needed to generate them automatically. Sign in and Import it from your vault list above instead."));
		return true;
	}
	Phase = EPhase::ResolvingLocal;
	EmitPhase(TEXT("Resolving"), TEXT("Matching this bundle to your vault..."), -1.0f);
	VaultClient->ListVault();
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
	return FMantlePlaceSha256::HexDigest(Bytes);
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
	VaultClient->GetPresignedBundleUrl(ActiveItem.OrderId);
}

void UMantlePlaceVaultImportOrchestrator::BeginLevelChoice()
{
	Phase = EPhase::ChoosingLevel;
	bAttemptedMaterializeRecovery = true;
	LevelChoice = FMantlePlaceLevelChoice::For(ActiveItem);
	if (!LevelChoice.IsValid())
	{
		FailImport(TEXT("This order's levels could not be read, so no level can be downloaded."));
		return;
	}
	const TSharedRef<FMantlePlaceLevelChoice> Choice = LevelChoice.ToSharedRef();
	TWeakObjectPtr<UMantlePlaceVaultImportOrchestrator> WeakThis(this);
	Choice->OnConfirmed = [WeakThis, WeakChoice = TWeakPtr<FMantlePlaceLevelChoice>(Choice)](const FString& Level)
	{
		FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateLambda([WeakThis, WeakChoice, Level](float)
		{
			UMantlePlaceVaultImportOrchestrator* Self = WeakThis.Get();
			if (Self != nullptr && Self->Phase == EPhase::ChoosingLevel && Self->LevelChoice.IsValid()
				&& Self->LevelChoice == WeakChoice.Pin())
			{
				Self->DownloadLevel(Level);
			}
			return false;
		}));
	};

	EmitPhase(TEXT("Choosing"), FString::Printf(TEXT("Choose a %s, then %s."),
		*FMantlePlaceLevelChoice::Heading().ToLower(), *FMantlePlaceLevelChoice::DownloadWord()), -1.0f);
	if (OnOfferLevelChoiceNative.IsBound() && OnOfferLevelChoiceNative.Execute(Choice))
	{
		return;
	}

	if (!Choice->Confirm())
	{
		FailImport(TEXT("This order states no recommended level, and there is no window to choose one in. "
			"Import it from the Mantle Place panel and choose a level."));
	}
}

void UMantlePlaceVaultImportOrchestrator::DownloadLevel(const FString& Level)
{
	if (BundleCache->IsCachedAndValidAtLevel(ActiveItem, Level))
	{
		FMantlePlacePresignedDownload Kept;
		Kept.Level = Level;
		Kept.bIsView = true;
		Phase = EPhase::Downloading;
		EmitPhase(TEXT("Downloading"), FString::Printf(TEXT("The %s level is already downloaded."), *Level), -1.0f);
		BundleCache->DownloadBundle(ActiveItem, Kept);
		return;
	}
	Phase = EPhase::Presigning;
	EmitPhase(TEXT("Downloading"), FString::Printf(TEXT("Requesting the %s level..."), *Level), -1.0f);
	VaultClient->GetPresignedLevelUrl(ActiveItem.OrderId, Level, FMantlePlaceManifestThreeLogic::RequestedFiles());
}

float UMantlePlaceVaultImportOrchestrator::ResolvePollIntervalSeconds() const
{
	return MaterializePollIntervalSeconds > 0.0f
		? UMantlePlaceEditorSettings::UsableMaterializePollIntervalSeconds(MaterializePollIntervalSeconds)
		: UMantlePlaceEditorSettings::ResolveMaterializePollIntervalSeconds();
}

int32 UMantlePlaceVaultImportOrchestrator::ResolveMaxPolls() const
{
	return MaterializeMaxPolls > 0
		? UMantlePlaceEditorSettings::UsableMaterializeMaxPolls(MaterializeMaxPolls)
		: UMantlePlaceEditorSettings::ResolveMaterializeMaxPolls();
}

int32 UMantlePlaceVaultImportOrchestrator::ResolveMaxConsecutivePollFailures() const
{
	return MaxConsecutivePollFailures > 0
		? UMantlePlaceEditorSettings::UsableMaxConsecutivePollFailures(MaxConsecutivePollFailures)
		: UMantlePlaceEditorSettings::ResolveMaxConsecutivePollFailures();
}

void UMantlePlaceVaultImportOrchestrator::SchedulePoll()
{
	UnschedulePoll();

	if (PollCount >= ResolveMaxPolls())
	{
		FailImport(LastOutstanding.Num() == 0
		               ? TEXT("Timed out waiting for the Unreal formats to generate.")
		               : FString::Printf(
		                     TEXT("Timed out waiting for the Unreal formats to generate. Still missing: %s"),
		                     *FString::Join(LastOutstanding, TEXT(", "))));
		return;
	}

	const float Delay = ResolvePollIntervalSeconds();
	TWeakObjectPtr<UMantlePlaceVaultImportOrchestrator> WeakThis(this);
	PollTicker = FTSTicker::GetCoreTicker().AddTicker(TEXT("MantlePlaceMaterializePoll"), Delay,
		[WeakThis](float) -> bool
		{
			if (UMantlePlaceVaultImportOrchestrator* Self = WeakThis.Get())
			{
				Self->PollTicker.Reset();
				Self->DoPoll();
			}
			return false;
		});
}

void UMantlePlaceVaultImportOrchestrator::DoPoll()
{
	if (Phase != EPhase::Polling)
	{
		return;
	}
	++PollCount;
	VaultClient->GetMaterializeStatus(ActiveItem.OrderId, ActiveRequestedTokens);
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
		return;
	}
	if (!bSuccess)
	{
		FailImport(Message.IsEmpty() ? TEXT("Could not start generating Unreal formats.") : Message);
		return;
	}

	const bool bRouteStart = bRouteStartPending;
	bRouteStartPending = false;
	if (bRouteStart && UMantlePlaceVaultClient::DecideImportRoute(Start) == EMantlePlaceImportRoute::DownloadView)
	{
		bViewRequested = true;
		bViewDeliveryKnown = Start.Outcome == EMantlePlaceMaterializeStartOutcome::NothingToDo;
		ViewDelivered = bViewDeliveryKnown ? Start.Tokens : TArray<FString>();
		Phase = EPhase::Presigning;
		EmitPhase(TEXT("Downloading"), TEXT("This order is complete - requesting Unreal's files..."), -1.0f);
		VaultClient->GetPresignedViewUrl(ActiveItem.OrderId);
		return;
	}
	if (Start.Outcome == EMantlePlaceMaterializeStartOutcome::DeliveryUnknown)
	{
		FailImport(TEXT("The platform could not read what this bundle has. Try again in a moment."));
		return;
	}

	if (Start.Outcome == EMantlePlaceMaterializeStartOutcome::NothingToDo)
	{
		EmitPhase(TEXT("Generated"), TEXT("This bundle already has everything the plugin needs."), 1.0f);
		Phase = EPhase::Relisting;
		VaultClient->ListVault();
		return;
	}

	ActiveJobId = Start.JobId;
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
		if (++ConsecutivePollFailures > ResolveMaxConsecutivePollFailures())
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
		EmitPhase(TEXT("Generated"),
		          Status.Unproducible.Num() == 0
		              ? TEXT("Unreal formats are ready.")
		              : FString::Printf(TEXT("Unreal formats are ready. %d not available for this area."),
		                                Status.Unproducible.Num()),
		          1.0f);
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
	OnVaultListed.Broadcast(bSuccess, Bundles, Message);

	if (Phase == EPhase::AutoPicking)
	{
		Phase = EPhase::Idle;
		if (!bSuccess)
		{
			FailImport(FString::Printf(TEXT("Could not list the vault: %s"), *Message));
			return;
		}
		const FMantlePlaceVaultItem* Pick = Bundles.FindByPredicate(
			[](const FMantlePlaceVaultItem& Bundle) { return UMantlePlaceVaultClient::IsBundleIncomplete(Bundle); });
		if (Pick == nullptr && Bundles.Num() > 0)
		{
			Pick = &Bundles[0];
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
		Phase = EPhase::Idle;
		if (!bSuccess)
		{
			FailImport(FString::Printf(TEXT("Could not list your vault to match the bundle: %s"), *Message));
			return;
		}

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

		ActiveItem = *Match;
		BeginItemImport();
		return;
	}

	if (Phase != EPhase::Relisting)
	{
		return;
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

	if (bViewRequested)
	{
		bViewRequested = false;
		switch (UMantlePlaceVaultClient::DecideViewFetch(Download, bViewDeliveryKnown, ViewDelivered))
		{
		case EMantlePlaceViewFetch::Prepare:
			UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("The view does not show the order complete; running the Prepare."));
			Phase = EPhase::Materializing;
			EmitPhase(TEXT("Generating"), FString::Printf(TEXT("Requesting Unreal formats (%s)..."), *ActiveScope), -1.0f);
			VaultClient->RequestMaterialize(ActiveItem.OrderId, ActiveScope);
			return;

		case EMantlePlaceViewFetch::WholeBundleInstead:
			UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("The view lacks a file the import reads from outside Unreal's block; fetching the whole bundle."));
			[[fallthrough]];
		case EMantlePlaceViewFetch::WholeArchive:
			Phase = EPhase::Relisting;
			VaultClient->ListVault();
			return;

		case EMantlePlaceViewFetch::View:
		default:
			ViewManifestSha256 = Download.ManifestSha256;
			break;
		}
	}

	if (!Download.Level.IsEmpty())
	{
		ViewManifestSha256 = Download.ManifestSha256;
	}
	LevelAbsentFiles = Download.AbsentFiles;

	Phase = EPhase::Downloading;
	EmitPhase(TEXT("Downloading"),
		!Download.Level.IsEmpty() ? FString::Printf(TEXT("Downloading the %s level..."), *Download.Level)
		: Download.bIsView ? FString(TEXT("Downloading Unreal's files...")) : FString(TEXT("Downloading the bundle...")), 0.0f);
	BundleCache->DownloadBundle(ActiveItem, Download);
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

	const FString ExpectedManifestSha256 = ViewManifestSha256;
	ViewManifestSha256.Reset();
	if (!ExpectedManifestSha256.IsEmpty() && BundleCache->CachedHoldsView(ActiveItem))
	{
		FString ActualManifestSha256;
		FString DigestError;
		if (!UMantlePlaceImporterLibrary::ReadManifestSha256(LocalBundlePath, ActualManifestSha256, DigestError)
			|| !ActualManifestSha256.Equals(ExpectedManifestSha256, ESearchCase::IgnoreCase))
		{
			BundleCache->EvictCache(ActiveItem);
			FailImport(TEXT("The download's manifest did not match its checksum and was discarded. Try again."));
			return;
		}
	}

	const TArray<FString> RefusedFiles = MoveTemp(LevelAbsentFiles);
	LevelAbsentFiles.Reset();
	if (RefusedFiles.Num() > 0)
	{
		TArray<FString> Listed;
		FString ListedError;
		if (UMantlePlaceImporterLibrary::FindListedFiles(LocalBundlePath, RefusedFiles, Listed, ListedError) && Listed.Num() > 0)
		{
			BundleCache->EvictCache(ActiveItem);
			FailImport(FString::Printf(
				TEXT("The platform refused %s as not in this bundle, but the bundle's manifest lists it. Nothing was "
					"imported: this is a fault on the platform's side, not a layer the site lacks. Try again, and "
					"report it if it repeats."),
				*FString::Join(Listed, TEXT(", "))));
			return;
		}
	}

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

	FMantlePlaceImportResult Result = RunBundleImport(LocalBundlePath);
	Result.Level = BundleCache->InspectCache(ActiveItem).Level;
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
	LevelAbsentFiles.Reset();
	LevelChoice.Reset();
	ScriptedLevel.Reset();
	if (Steps.IsValid())
	{
		Steps->Finish(FPlatformTime::Seconds());
		Steps.Reset();
	}
	UE_LOG(LogMantlePlaceVaultImport, Log, TEXT("Vault import %s: %s"),
		bSuccess ? TEXT("succeeded") : (Result.bCancelled ? TEXT("cancelled") : TEXT("failed")), *Result.Message);
	OnImportFinished.Broadcast(bSuccess, Result);
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
	if (Steps.IsValid() && Phase != EPhase::Importing)
	{
		Steps->SetActivity(Fraction >= 0.0f && Message.IsEmpty()
			? FString::Printf(TEXT("%.0f%%"), Fraction * 100.0f)
			: Message);
	}
	OnImportPhase.Broadcast(PhaseLabel, Message, Fraction);
}

void UMantlePlaceVaultImportOrchestrator::BeginSteps()
{
	Steps = MakeShared<FMantlePlaceImportSteps>();
	OnImportStepsNative.Broadcast(Steps.ToSharedRef());
}

FMantlePlaceImportResult UMantlePlaceVaultImportOrchestrator::RunBundleImport(const FString& Path)
{
	if (Steps.IsValid())
	{
		Steps->SetActivity(FString());
		Steps->End(FName(TEXT("download")), true, FPlatformTime::Seconds());
	}
	return UMantlePlaceImporterLibrary::ImportVaultPackageWithSteps(Path, ActiveMode, Steps.Get());
}

void UMantlePlaceVaultImportOrchestrator::CancelImport()
{
	if (Phase == EPhase::Idle)
	{
		return;
	}
	if (Steps.IsValid())
	{
		Steps->RequestCancel();
	}
	if (Phase == EPhase::Importing)
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
	Result.bCancelled = true;
	Result.Message = TEXT("Import cancelled before the bundle was imported. Nothing in the level changed.");
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
