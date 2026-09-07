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
	 * The identity everything one import generates is keyed on: the ORDER when the bundle names
	 * one, and otherwise a content hash of the bundle's own manifest.
	 *
	 * Never the job id, which the manifest itself documents as changing on every rebuild. Keyed on
	 * a job, re-materialising an order produced a second folder and a second landscape while the
	 * re-import wipe looked at a path nobody was using any more. See ADR 0002.
	 *
	 * The fallback is a hash because a bundle with no order id is a locally produced or admin
	 * bundle, where "a different build is a different thing" is the honest semantics — there is no
	 * order for it to be a rebuild OF. The manifest is what gets hashed, not the whole zip: it
	 * declares every artifact's own sha256, so it is a content hash of the bundle by proxy, and it
	 * is already in memory when the importer needs this.
	 *
	 * Returns empty when neither input is usable. An empty identity is refusable, not a default —
	 * see IsUsableIdentity.
	 */
	FString ResolveIdentity(const FString& OrderId, const FString& ManifestSha256);

	/**
	 * Whether an identity is safe to key content on AND safe as a single path segment.
	 *
	 * Both halves matter and the second is the sharp one. An order id arrives from bundle JSON, and
	 * the identity becomes a package-path segment of a directory the importer FORCE-DELETES on
	 * re-import. An empty one collapses that path to the content root, so the delete reaches every
	 * import the project has; a `..` in one walks it somewhere else entirely. Refuse rather than
	 * sanitise: a bundle whose identity is unusable is a bundle we do not understand, and quietly
	 * rewriting it into something that looks fine is how the delete ends up pointed at the wrong
	 * directory with nobody having decided that.
	 */
	bool IsUsableIdentity(const FString& Identity);

	/**
	 * The short form of an import identity, as it appears in a folder name and an actor label.
	 *
	 * Truncating is a readability choice with a sharp edge: two identities sharing a short form
	 * share a folder, and that folder is force-deleted on re-import. Whatever calls this owes the
	 * caller a check that the folder it is about to wipe belongs to the identity it thinks it does
	 * — that is what the provenance record in MantlePlaceImportProvenance is for.
	 */
	FString ShortIdentity(const FString& Identity);

	// --- Placement in the level -----------------------------------------------------------------

	/**
	 * The World Outliner folder every actor of one import goes into: `MantlePlace/<shortIdentity>`.
	 *
	 * Deliberately NOT derived from the configured content root. Outliner folders have no project
	 * layout policy to collide with, so there is nothing for a setting to resolve — and keeping the
	 * top segment fixed leaves one predictable marker for support no matter how a project is
	 * configured. Which is also why the actor labels keep their prefix: see ADR 0003.
	 */
	FString OutlinerFolder(const FString& Identity);

	/**
	 * The actor tag that marks an actor as belonging to one import: `mantleplace_import=<identity>`.
	 *
	 * This, not the label, is what re-import matches on. A label is a thing a user edits — renaming
	 * an actor in the outliner used to break that user's own next re-import, silently, and then
	 * stack a duplicate landscape on the first. A tag is not surfaced for editing, carries the FULL
	 * identity rather than the truncation, and survives the actor being dragged to another folder.
	 *
	 * Its ABSENCE is also the legacy signal: an actor from 0.3.0 or earlier has an `MP_*` label and
	 * no tag at all, which is how the importer recognises content it must not silently adopt.
	 */
	FString ImportTag(const FString& Identity);

	/** The tag prefix alone, for finding this plugin's actors regardless of identity. */
	FString ImportTagPrefix();

	// --- Package paths ------------------------------------------------------------------------

	/** The default content root, before any project setting overrides it. */
	const TCHAR* DefaultContentRoot();

	/**
	 * Whether a configured content root is usable as the parent of a directory the importer
	 * force-deletes.
	 *
	 * Requires a mount point and at least one segment beneath it — `/Game` alone is refused, and
	 * that refusal is the point rather than pedantry: the importer creates `<root>/<identity>` and
	 * deletes it, so a root of `/Game` is fine, but a root that is EMPTY or a bare `/` would put
	 * the delete somewhere it must never reach. No trailing slash, no relative segments.
	 */
	bool IsUsableContentRoot(const FString& ContentRoot);

	/** The configured root when it is usable, and the default when it is not. */
	FString ResolveContentRoot(const FString& ConfiguredRoot);

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

	/**
	 * `MI_Drape` — the drape material instance.
	 *
	 * The identity does NOT appear here. It is already the folder this sits in, and repeating it in
	 * the leaf is what made `MI_Drape_a1b2c3d4` unreadable in a material picker.
	 */
	FString DrapeMaterialName();

	/**
	 * `LI_<material>` — the layer info asset for one paint layer.
	 *
	 * The asset is prefixed; the layer's own name inside it is NOT. That divergence is deliberate
	 * and load-bearing: the layer name is what a landscape material's LandscapeLayerBlend nodes
	 * bind to, so it stays the platform's material name verbatim (`HPS-33`).
	 */
	FString LayerInfoName(const FString& Material);

	/** `DT_TreePoints` — the foliage-point data table. The folder carries the identity. */
	FString TreePointsTableName();

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
