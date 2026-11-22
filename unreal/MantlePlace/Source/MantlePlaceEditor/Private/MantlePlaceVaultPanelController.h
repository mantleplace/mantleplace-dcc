// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceAuthTypes.h"
#include "MantlePlaceClientLogic.h"
#include "MantlePlaceVaultImportOrchestrator.h"
#include "MantlePlaceVaultPanelController.generated.h"

class UMantlePlaceVaultImportOrchestrator;

/**
 * Module-internal bridge between the native Slate vault panel (SMantlePlaceVaultPanel) and the
 * UObject vault-import orchestrator. It exists to solve two problems a raw SCompoundWidget cannot:
 *
 *   1. The orchestrator's completion events are DYNAMIC (BlueprintAssignable) delegates - AddDynamic
 *      requires a UObject with UFUNCTION handlers, which a Slate widget is not. This controller binds
 *      them and re-emits plain native multicast delegates the panel binds with AddSP.
 *   2. A Slate widget does not keep a UObject alive. The panel holds this controller via
 *      TStrongObjectPtr, and this controller UPROPERTY-owns the orchestrator, so the whole chain
 *      (orchestrator -> vault client + bundle cache) stays GC-rooted for the panel's lifetime.
 *
 * It is the ported, UI-free body of the former UMantlePlaceVaultEUWBase.
 */
UCLASS()
class UMantlePlaceVaultPanelController : public UObject
{
	GENERATED_BODY()

public:
	DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnVaultListed, bool, const TArray<FMantlePlaceVaultItem>&, const FString&);
	DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnImportPhase, const FString&, const FString&, float);
	DECLARE_MULTICAST_DELEGATE_TwoParams(FMantlePlaceOnImportFinished, bool, const FMantlePlaceImportResult&);
	DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnAuthChanged, EMantlePlaceAuthState);

	FMantlePlaceOnVaultListed OnVaultListed;
	FMantlePlaceOnImportPhase OnImportPhase;
	FMantlePlaceOnImportFinished OnImportFinished;
	FMantlePlaceOnAuthChanged OnAuthChanged;

	FMantlePlaceOnClientUpdateRequiredNative OnClientUpdateRequired;

	FMantlePlaceOnImportStepsNative OnImportSteps;

	FMantlePlaceOnOfferLevelChoiceNative OnOfferLevelChoice;

	void Initialize();

	void Shutdown();

	bool IsSignedIn() const;
	void TryRestoreSession();
	FString GetLastAuthError() const;
	bool IsBusy() const;
	void SignIn();
	void SignOut();
	EMantlePlaceAuthState GetAuthState() const;
	void RefreshVaultList();

	bool StartImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode);

	bool StartLocalImport(const FString& ZipPath, EMantlePlaceImportMode Mode);

	void CancelImport();

	bool IsActiveItem(const FMantlePlaceVaultItem& Item) const;

private:
	UFUNCTION() void HandleVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	UFUNCTION() void HandleImportPhase(const FString& Phase, const FString& Message, float Fraction);
	UFUNCTION() void HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result);
	UFUNCTION() void HandleAuthChanged(EMantlePlaceAuthState NewState);
	void HandleClientUpdateRequired(const FMantlePlaceClientUpdate& Update);
	void HandleImportSteps(TSharedRef<FMantlePlaceImportSteps> Steps);
	bool HandleOfferLevelChoice(TSharedRef<FMantlePlaceLevelChoice> Choice);

	UPROPERTY()
	TObjectPtr<UMantlePlaceVaultImportOrchestrator> Orchestrator;

	FString ActiveOrderId;
};
