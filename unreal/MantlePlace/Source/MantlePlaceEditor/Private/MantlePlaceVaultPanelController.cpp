// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultPanelController.h"

#include "MantlePlaceVaultImportOrchestrator.h"

void UMantlePlaceVaultPanelController::Initialize()
{
	if (!Orchestrator)
	{
		Orchestrator = NewObject<UMantlePlaceVaultImportOrchestrator>(this);
	}
	Orchestrator->OnVaultListed.AddUniqueDynamic(this, &UMantlePlaceVaultPanelController::HandleVaultListed);
	Orchestrator->OnImportPhase.AddUniqueDynamic(this, &UMantlePlaceVaultPanelController::HandleImportPhase);
	Orchestrator->OnImportFinished.AddUniqueDynamic(this, &UMantlePlaceVaultPanelController::HandleImportFinished);
	Orchestrator->OnAuthChanged.AddUniqueDynamic(this, &UMantlePlaceVaultPanelController::HandleAuthChanged);
}

void UMantlePlaceVaultPanelController::Shutdown()
{
	if (Orchestrator)
	{
		Orchestrator->OnVaultListed.RemoveAll(this);
		Orchestrator->OnImportPhase.RemoveAll(this);
		Orchestrator->OnImportFinished.RemoveAll(this);
		Orchestrator->OnAuthChanged.RemoveAll(this);
		// Closing the tab mid-import must stop the in-flight poll/download - BeginDestroy alone does not.
		Orchestrator->CancelImport();
	}
	OnVaultListed.Clear();
	OnImportPhase.Clear();
	OnImportFinished.Clear();
	OnAuthChanged.Clear();
	ActiveOrderId.Reset();
}

bool UMantlePlaceVaultPanelController::IsSignedIn() const
{
	return Orchestrator && Orchestrator->IsSignedIn();
}

bool UMantlePlaceVaultPanelController::IsBusy() const
{
	return Orchestrator && Orchestrator->IsBusy();
}

void UMantlePlaceVaultPanelController::SignIn()
{
	if (Orchestrator) { Orchestrator->SignIn(); }
}

void UMantlePlaceVaultPanelController::SignOut()
{
	if (Orchestrator) { Orchestrator->SignOut(); }
}

EMantlePlaceAuthState UMantlePlaceVaultPanelController::GetAuthState() const
{
	return Orchestrator ? Orchestrator->GetAuthState() : EMantlePlaceAuthState::Unauthenticated;
}

void UMantlePlaceVaultPanelController::RefreshVaultList()
{
	if (Orchestrator) { Orchestrator->RefreshVaultList(); }
}

bool UMantlePlaceVaultPanelController::StartImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode)
{
	if (!Orchestrator)
	{
		return false;
	}
	// Vault-row imports honor the panel's mode combo (Landscape/Mesh/Both), same as local zips; the
	// "unreal" scope resolves to the explicit targeted-token list in FMantlePlaceVaultLogic. The
	// orchestrator materializes first for a BASE bundle, or imports directly for a ready UNREAL bundle.
	const bool bStarted = Orchestrator->StartVaultImport(Item, Mode, TEXT("unreal"));
	if (bStarted)
	{
		ActiveOrderId = Item.OrderId;
	}
	return bStarted;
}

bool UMantlePlaceVaultPanelController::StartLocalImport(const FString& ZipPath, EMantlePlaceImportMode Mode)
{
	if (!Orchestrator)
	{
		return false;
	}
	// A local import is not a vault row, so no ActiveOrderId is set (progress shows in the local
	// section + toasts, not on a list row).
	return Orchestrator->StartLocalImport(ZipPath, Mode);
}

bool UMantlePlaceVaultPanelController::IsActiveItem(const FMantlePlaceVaultItem& Item) const
{
	return !ActiveOrderId.IsEmpty() && Item.OrderId == ActiveOrderId;
}

void UMantlePlaceVaultPanelController::HandleVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message)
{
	OnVaultListed.Broadcast(bSuccess, Bundles, Message);
}

void UMantlePlaceVaultPanelController::HandleImportPhase(const FString& Phase, const FString& Message, float Fraction)
{
	OnImportPhase.Broadcast(Phase, Message, Fraction);
}

void UMantlePlaceVaultPanelController::HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result)
{
	ActiveOrderId.Reset();
	OnImportFinished.Broadcast(bSuccess, Result);
	// Re-list so a freshly materialized BASE bundle flips Base -> Unreal and every row's action
	// re-enables (mirrors the former EUW HandleImportFinished). Only when signed in - a signed-out
	// local-zip import has no vault to refresh, and listing would just fail.
	if (Orchestrator && Orchestrator->IsSignedIn()) { Orchestrator->RefreshVaultList(); }
}

void UMantlePlaceVaultPanelController::HandleAuthChanged(EMantlePlaceAuthState NewState)
{
	OnAuthChanged.Broadcast(NewState);
}
