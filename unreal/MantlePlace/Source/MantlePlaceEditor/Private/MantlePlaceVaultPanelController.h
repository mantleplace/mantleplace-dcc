// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "MantlePlaceVaultTypes.h"   // FMantlePlaceVaultItem
#include "MantlePlaceImportTypes.h"  // FMantlePlaceImportResult
#include "MantlePlaceAuthTypes.h"    // EMantlePlaceAuthState
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
	// Native (non-dynamic) events the Slate panel binds via AddSP. Signatures mirror the
	// orchestrator's dynamic delegates so the panel handlers read identically to the old EUW's.
	DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnVaultListed, bool /*bSuccess*/, const TArray<FMantlePlaceVaultItem>& /*Bundles*/, const FString& /*Message*/);
	DECLARE_MULTICAST_DELEGATE_ThreeParams(FMantlePlaceOnImportPhase, const FString& /*Phase*/, const FString& /*Message*/, float /*Fraction*/);
	DECLARE_MULTICAST_DELEGATE_TwoParams(FMantlePlaceOnImportFinished, bool /*bSuccess*/, const FMantlePlaceImportResult& /*Result*/);
	DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnAuthChanged, EMantlePlaceAuthState /*NewState*/);

	FMantlePlaceOnVaultListed OnVaultListed;
	FMantlePlaceOnImportPhase OnImportPhase;
	FMantlePlaceOnImportFinished OnImportFinished;
	FMantlePlaceOnAuthChanged OnAuthChanged;

	/** Create the orchestrator and bind its dynamic delegates (idempotent). Does not list. */
	void Initialize();

	/** Unbind the orchestrator, cancel any in-flight import, and clear the native delegates. */
	void Shutdown();

	//~ Pass-throughs the panel / rows call.
	bool IsSignedIn() const;
	bool IsBusy() const;
	void SignIn();
	void SignOut();
	EMantlePlaceAuthState GetAuthState() const;
	void RefreshVaultList();

	/** Start Item's vault import (materialize->import for a BASE bundle, direct import for UNREAL).
	 *  Mode comes from the panel's Landscape/Mesh/Both combo — vault rows and local zips share it. */
	bool StartImport(const FMantlePlaceVaultItem& Item, EMantlePlaceImportMode Mode);

	/** Start a local-zip import (stage -> import, or materialize the missing Unreal formats first). */
	bool StartLocalImport(const FString& ZipPath, EMantlePlaceImportMode Mode);

	/** True while Item is the bundle currently importing (gates that row's progress line). */
	bool IsActiveItem(const FMantlePlaceVaultItem& Item) const;

private:
	UFUNCTION() void HandleVaultListed(bool bSuccess, const TArray<FMantlePlaceVaultItem>& Bundles, const FString& Message);
	UFUNCTION() void HandleImportPhase(const FString& Phase, const FString& Message, float Fraction);
	UFUNCTION() void HandleImportFinished(bool bSuccess, const FMantlePlaceImportResult& Result);
	UFUNCTION() void HandleAuthChanged(EMantlePlaceAuthState NewState);

	UPROPERTY()
	TObjectPtr<UMantlePlaceVaultImportOrchestrator> Orchestrator;

	/** OrderId of the bundle currently importing; empty when idle. */
	FString ActiveOrderId;
};
