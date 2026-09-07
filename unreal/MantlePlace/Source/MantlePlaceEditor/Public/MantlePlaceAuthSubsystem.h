// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "EditorSubsystem.h"
#include "MantlePlaceAuthSubsystem.generated.h"

class UMantlePlaceAuthSystemBase;

/**
 * Owns the editor's ONE auth session.
 *
 * The auth source used to be created by whoever needed it first - in practice
 * UMantlePlaceVaultImportOrchestrator::EnsureClients, whose ownership chain roots at the vault
 * panel WIDGET. That made the session's lifetime the tab's lifetime, so two vault tabs were two
 * independent sessions: each with its own access token in memory, each writing the same refresh
 * token file, and signing out in one leaving the other believing it was still signed in. The Revit
 * host has always been one session per process for exactly that reason; this is the same rule,
 * expressed the way the editor expresses process-wide state.
 *
 * An editor subsystem is also the only place that can restore a stored session without a Blueprint:
 * it has a defined startup moment that does not depend on a widget being constructed. That startup
 * restore is not here yet - it lands with the storage change - but this is the object that will own
 * it, which is why the hoist comes first.
 *
 * Nothing in here talks to the network or the secret store. It owns the auth object and hands it
 * out; every decision still lives in UMantlePlaceAuthSystemBase and, below that, in the pure
 * FMantlePlaceAuthLogic that the conformance corpus drives.
 */
UCLASS()
class MANTLEPLACEEDITOR_API UMantlePlaceAuthSubsystem : public UEditorSubsystem
{
	GENERATED_BODY()

public:
	//~ Begin USubsystem
	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;
	//~ End USubsystem

	/** The editor's auth source. Non-null for the life of the subsystem. */
	UMantlePlaceAuthSystemBase* GetAuthSystem() const { return AuthSystem; }

	/**
	 * The subsystem for the running editor, or null when there is no editor at all (a commandlet,
	 * or a cooked build that somehow loaded this module). Callers that can run headless must handle
	 * the null - see UMantlePlaceVaultImportOrchestrator::EnsureClients.
	 */
	static UMantlePlaceAuthSubsystem* Get();

private:
	UPROPERTY()
	TObjectPtr<UMantlePlaceAuthSystemBase> AuthSystem;
};
