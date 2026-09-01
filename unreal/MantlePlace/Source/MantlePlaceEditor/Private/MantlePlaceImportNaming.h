// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

class UObject;

namespace MantlePlaceImportNaming
{
	/**
	 * The name an imported asset should be GIVEN so it lands already conforming to the project's
	 * asset-naming standard (NativeStyleGuide §8: SM_/T_/MI_/M_ prefixes) — set it on
	 * UAssetImportTask::DestinationName. `SourceFile` is the file being imported; the asset would
	 * otherwise be named after its base filename, which is what the standard rejects.
	 *
	 * Prefer this to importing-then-renaming, ALWAYS, and not for tidiness: a rename inside the
	 * import's undo transaction silently destroys that transaction. FAssetRenameManager deletes the
	 * source object it moved away from, and ObjectTools resets the WHOLE undo buffer whenever the
	 * object it is deleting is referenced only by that buffer (ObjectTools.cpp: "only ref to this
	 * object is the transaction buffer, clear the transaction buffer"). An asset created inside
	 * ImportVaultPackage's FScopedTransaction is exactly such an object, so renaming it takes the
	 * whole import down with it — the level keeps the actors and Ctrl+Z does nothing. Measured
	 * 2026-08-30: one rename of the drape texture emptied a 523-actor import off the undo stack.
	 */
	FString ImportNameFor(const TCHAR* Prefix, const FString& SourceFile);

	/**
	 * Rename an already-imported asset to the same standard. The fallback for assets whose name we
	 * do not choose — Interchange sub-assets embedded in a source file (a glTF's materials and
	 * textures), which DestinationName does not reach.
	 *
	 * DANGEROUS INSIDE AN IMPORT TRANSACTION for the reason spelled out on ImportNameFor. Every
	 * asset the vault importer names itself goes through DestinationName instead; this is here so a
	 * future bundle whose glTF carries embedded assets still lands compliant names. If one ever
	 * does, the undo transaction it breaks is the cost — move the fix upstream (name them through
	 * the Interchange pipeline) rather than accepting the purge.
	 */
	void RenameToConvention(UObject* Asset);
}
