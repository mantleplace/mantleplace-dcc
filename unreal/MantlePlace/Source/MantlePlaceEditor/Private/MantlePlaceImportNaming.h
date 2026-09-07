// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

class UObject;

/**
 * Every name and package path the importer GIVES to something it creates is built here, and nowhere
 * else. Not for tidiness: the standard in `unreal/CLAUDE.md` is only a standard while it has one
 * point of definition. Spelled at each call site it drifts on the first patch that does not know
 * about it, and a naming regression compiles cleanly — nothing in CI builds this plugin, so the
 * compiler is not going to catch it and neither is a hosted gate.
 *
 * `tools/unreal-naming/check_generated_names.py` enforces the "nowhere else" half: a `/Game/`
 * literal, a bare prefix or an actor-label literal outside this file fails that gate.
 *
 * What does NOT belong here: names the importer READS rather than writes. A paint layer's name
 * arrives in the manifest and is applied verbatim (`HPS-33`), and the drape material template is
 * shipped plugin content addressed by its own mount point. Both are inputs, not names we chose.
 */
namespace MantlePlaceImportNaming
{
	/** Subfolders beneath one import's root. The set is closed; a new one is a new enumerator. */
	enum class ESubfolder : uint8
	{
		Imagery,
		Mesh,
		Buildings,
		CoverageRasters,
		Landcover,
	};

	/** The actors one import spawns, for label construction. Road splines are numbered separately. */
	enum class EActorKind : uint8
	{
		Landscape,
		Mesh,
		Buildings,
	};

	// --- Identity -----------------------------------------------------------------------------

	/**
	 * The short form of an import identity, as it appears in a folder name and an actor label.
	 *
	 * Truncating is a readability choice with a sharp edge: two identities sharing a short form
	 * share a folder, and that folder is force-deleted on re-import. Whatever calls this owes the
	 * caller a check that the folder it is about to wipe belongs to the identity it thinks it does.
	 */
	FString ShortIdentity(const FString& Identity);

	// --- Package paths ------------------------------------------------------------------------

	/** The default content root, before any project setting overrides it. */
	const TCHAR* DefaultContentRoot();

	/** `<ContentRoot>/<shortIdentity>` — everything one import generates lives beneath this. */
	FString ImportRoot(const FString& ContentRoot, const FString& Identity);

	/** `<ImportRoot>/<Subfolder>`. */
	FString SubfolderPath(const FString& ImportRootPath, ESubfolder Subfolder);

	/** `<PackagePath>/<AssetName>.<AssetName>` — an asset addressed inside a package directory. */
	FString ObjectPathIn(const FString& PackagePath, const FString& AssetName);

	/** `<PackageName>.<AssetName>` — an asset whose package name already ends in the asset. */
	FString ObjectPathOf(const FString& PackageName, const FString& AssetName);

	// --- Asset names --------------------------------------------------------------------------

	/**
	 * The name an imported asset should be GIVEN so it lands already conforming to the asset-naming
	 * standard in `unreal/CLAUDE.md` — set it on UAssetImportTask::DestinationName. `SourceFile` is
	 * the file being imported; the asset would otherwise be named after its base filename, which is
	 * what the standard rejects. Prefer the typed wrappers below to passing a prefix by hand.
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

	/** `T_<sourceBasename>` — an imported texture. */
	FString TextureName(const FString& SourceFile);

	/** `SM_<sourceBasename>` — an imported static mesh. */
	FString StaticMeshName(const FString& SourceFile);

	/** `MI_Drape_<shortIdentity>` — the drape material instance. */
	FString DrapeMaterialName(const FString& Identity);

	/**
	 * `LI_<material>` — the layer info asset for one paint layer.
	 *
	 * The asset is prefixed; the layer's own name inside it is NOT. That divergence is deliberate
	 * and load-bearing: the layer name is what a landscape material's LandscapeLayerBlend nodes
	 * bind to, so it stays the platform's material name verbatim (`HPS-33`).
	 */
	FString LayerInfoName(const FString& Material);

	/** `DT_TreePoints_<shortIdentity>` — the foliage-point data table. */
	FString TreePointsTableName(const FString& Identity);

	/** `Tree_<index>` — one row of that table. */
	FString TreePointsRowName(int32 RowIndex);

	// --- Actor labels -------------------------------------------------------------------------

	/** `MP_<Kind>_<shortIdentity>`. See ADR 0003 for why the abbreviation is allowed here. */
	FString ActorLabel(EActorKind Kind, const FString& Identity);

	/** `MP_RoadSpline_<shortIdentity>_` — the common prefix of every road spline label. */
	FString RoadSplineLabelPrefix(const FString& Identity);

	/** `MP_RoadSpline_<shortIdentity>_<000>`. */
	FString RoadSplineLabel(const FString& Identity, int32 Index);

	// --- The fallback -------------------------------------------------------------------------

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
