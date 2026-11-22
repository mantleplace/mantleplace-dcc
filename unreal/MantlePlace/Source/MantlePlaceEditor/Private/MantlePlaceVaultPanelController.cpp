// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceVaultPanelController.h"

#include "MantlePlaceVaultImportOrchestrator.h"
#include "MantlePlaceLevelChoiceLogic.h"

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
	Orchestrator->OnClientUpdateRequiredNative.RemoveAll(this);
	Orchestrator->OnClientUpdateRequiredNative.AddUObject(this, &UMantlePlaceVaultPanelController::HandleClientUpdateRequired);
	Orchestrator->OnImportStepsNative.RemoveAll(this);
	Orchestrator->OnImportStepsNative.AddUObject(this, &UMantlePlaceVaultPanelController::HandleImportSteps);
	Orchestrator->OnOfferLevelChoiceNative.BindUObject(this, &UMantlePlaceVaultPanelController::HandleOfferLevelChoice);
}

void UMantlePlaceVaultPanelController::Shutdown()
{
	if (Orchestrator)
	{
		Orchestrator->OnVaultListed.RemoveAll(this);
		Orchestrator->OnImportPhase.RemoveAll(this);
		Orchestrator->OnImportFinished.RemoveAll(this);
		Orchestrator->OnAuthChanged.RemoveAll(this);
		Orchestrator->OnClientUpdateRequiredNative.RemoveAll(this);
		Orchestrator->OnImportStepsNative.RemoveAll(this);
		Orchestrator->OnOfferLevelChoiceNative.Unbind();
		Orchestrator->CancelImport();
	}
	OnVaultListed.Clear();
	OnImportPhase.Clear();
	OnImportFinished.Clear();
	OnAuthChanged.Clear();
	OnClientUpdateRequired.Clear();
	OnImportSteps.Clear();
	OnOfferLevelChoice.Unbind();
	ActiveOrderId.Reset();
}

bool UMantlePlaceVaultPanelController::IsSignedIn() const
{
	return Orchestrator && Orchestrator->IsSignedIn();
}

void UMantlePlaceVaultPanelController::TryRestoreSession()
{
	if (Orchestrator) { Orchestrator->TryRestoreSession(); }
}

FString UMantlePlaceVaultPanelController::GetLastAuthError() const
{
	return Orchestrator ? Orchestrator->GetLastAuthError() : FString();
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
	return Orchestrator->StartLocalImport(ZipPath, Mode);
}

void UMantlePlaceVaultPanelController::CancelImport()
{
	if (Orchestrator) { Orchestrator->CancelImport(); }
}

void UMantlePlaceVaultPanelController::HandleImportSteps(TSharedRef<FMantlePlaceImportSteps> Steps)
{
	OnImportSteps.Broadcast(Steps);
}

bool UMantlePlaceVaultPanelController::HandleOfferLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice)
{
	return OnOfferLevelChoice.IsBound() && OnOfferLevelChoice.Execute(Choice);
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
	if (Orchestrator && Orchestrator->IsSignedIn()) { Orchestrator->RefreshVaultList(); }
}

void UMantlePlaceVaultPanelController::HandleAuthChanged(EMantlePlaceAuthState NewState)
{
	OnAuthChanged.Broadcast(NewState);
}

void UMantlePlaceVaultPanelController::HandleClientUpdateRequired(const FMantlePlaceClientUpdate& Update)
{
	OnClientUpdateRequired.Broadcast(Update);
}
