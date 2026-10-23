// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class UWorld;

/** What Prepare for handover did to a level. */
struct FMantlePlaceHandoverReport
{
	/** Labels of the ODbL actors removed (every one, swapped ones included). */
	TArray<FString> Removed;
	/** Labels of the removed actors a Produced Work of the same group stands in for. */
	TArray<FString> Swapped;
	/** Labels of the credit actors written or refreshed. */
	TArray<FString> CreditActors;
	/** Anything the user should know that is not a count. */
	TArray<FString> Notes;

	/** A few lines for a dialog, a log and a Blueprint result. */
	FString Summary() const;
};

/**
 * Prepare for handover: the one editor command that makes a level safe to ship or share.
 *
 * Everything the import filed under ODbL (an actor imported from `database` files, a missing role
 * included) is removed, or swapped for the Produced Work of the same group already in the level
 * where there is one; and every order that owes a credit gets a credit actor carrying it. It works
 * from the tags the import wrote, on the level alone: no bundle has to be open, and a level a
 * colleague saved is as good as the one just imported.
 *
 * Inside one undo transaction, so Ctrl+Z puts the level back. Nothing is saved: like an import, the
 * result is for the user to save. The decisions are MantlePlaceOdblLogic's (PlanHandover).
 */
namespace MantlePlaceHandover
{
FMantlePlaceHandoverReport Prepare(UWorld* World);

/** The editor world's Prepare, logged. What the Tools menu entry and the console command run. */
FMantlePlaceHandoverReport PrepareEditorWorld();
}
