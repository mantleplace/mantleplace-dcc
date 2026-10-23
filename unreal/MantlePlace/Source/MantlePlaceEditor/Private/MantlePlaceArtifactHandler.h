// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceIntegrityLogic.h"

class AActor;
class FJsonObject;
class UDataTable;
class UTexture2D;
class FZipArchiveReader;
class UMaterialInstanceConstant;
class UWorld;
struct FMantlePlaceVaultManifest;
namespace MantlePlaceImportTiming
{
class FTimeline;
}

/**
 * Which artifacts an import builds, as DATA. A set of exclusions rather than an enum or a list of
 * flags: a new artifact is included by default, so registering a handler is all it takes, and a
 * selection UI is a set of checkboxes writing into `Excluded`. EMantlePlaceImportMode survives as a
 * way to BUILD one (FromMode), nothing more.
 *
 * Only an artifact that is a choice between alternatives is ever excluded by Mode: the terrain
 * Landscape and the terrain Mesh. Everything else describes the ground rather than representing it
 * and imports whenever the bundle ships it -- except the foliage scatter, which is generated content
 * rather than the bundle's, and is opt-in: FromMode leaves it out unless asked.
 */
struct FMantlePlaceImportSelection
{
	TSet<FName> Excluded;

	bool Includes(FName ArtifactId) const { return !Excluded.Contains(ArtifactId); }

	/** Every artifact the bundle ships. */
	static FMantlePlaceImportSelection Everything() { return FMantlePlaceImportSelection(); }

	/** The selection the three-way Mode implies, with the foliage scatter only when opted in. */
	static FMantlePlaceImportSelection FromMode(EMantlePlaceImportMode Mode, bool bFoliageScatter = false);
};

/** The ids a selection is built from. Every other artifact's id is private to its own handler. */
namespace MantlePlaceArtifactId
{
FName Landscape();
FName TerrainMesh();
/** The PCG foliage scatter: opt-in, so FromMode excludes it unless asked. */
FName FoliageScatter();
}

/** What a handler's ParseFields is given: the manifest's root and its `hosts.unreal` block. */
struct FMantlePlaceParseInput
{
	const TSharedPtr<FJsonObject>& Root;
	/** `hosts.unreal`, or an invalid pointer when the bundle has none. Handlers that read it no-op then. */
	const TSharedPtr<FJsonObject>& Unreal;
};

/**
 * Everything one import run shares across its handlers. A handler reads it, appends to the log and
 * the result, and never reaches for a global: that is what lets a headless test drive the whole run
 * with fake handlers and no bundle.
 */
struct FMantlePlaceImportContext
{
	FMantlePlaceImportContext(
		UWorld* InWorld, const FMantlePlaceVaultManifest& InManifest, const FZipArchiveReader* InReader,
		const FMantlePlaceImportSelection& InSelection, MantlePlaceImportTiming::FTimeline& InTimeline,
		FMantlePlaceImportResult& InResult, TArray<FString>& InLog)
		: World(InWorld), Manifest(InManifest), Reader(InReader), Selection(InSelection)
		, Timeline(InTimeline), Result(InResult), Log(InLog)
	{
	}

	UWorld* World;
	const FMantlePlaceVaultManifest& Manifest;
	/** The open bundle. Null only in a headless test whose handlers read no payload. */
	const FZipArchiveReader* Reader;
	const FMantlePlaceImportSelection& Selection;
	MantlePlaceImportTiming::FTimeline& Timeline;
	FMantlePlaceImportResult& Result;
	TArray<FString>& Log;

	FString Identity;
	FString TempDir;
	FString DestPackagePath;
	/** The resolved content root; the shared Data Layer assets live beneath it. Empty means the default. */
	FString ContentRoot;

	/**
	 * The layer family of the handler running now, set by the orchestration before each Prepare,
	 * Import and Finalize, so Claim files every actor a handler spawns under that handler's family
	 * without a single call site having to say so.
	 */
	EMantlePlaceLayerFamily Family = EMantlePlaceLayerFamily::None;

	/**
	 * The files the handler running now makes its actors from, and its equivalence group, set by the
	 * orchestration beside Family. Claim reads each file's `odbl_role` from the manifest: an actor made
	 * from `database` files (a missing role included) lands in the ODbL outliner folder and Data Layer.
	 * Empty means the actor is made from no file, and is not ODbL content.
	 */
	TArray<FString> SourcePaths;
	FName EquivalenceGroup;

	// Shared between handlers by ORDER: the drape handler runs first and leaves its material for the
	// geometry that follows; every geometry handler leaves its actor for the drape's coverage report.
	UMaterialInstanceConstant* DrapeMic = nullptr;
	TArray<AActor*> DrapeTargets;

	// The foliage scatter reads what the coverage rasters and the tree points left: the textures by
	// their manifest layer key (`canopy_height`, `water_mask`, `worldcover`), and the table.
	TMap<FString, UTexture2D*> CoverageTextures;
	UDataTable* TreePointsTable = nullptr;

	/** False once any requested artifact failed. Optional artifacts never clear it. */
	bool bAllRequestedSucceeded = true;

	/** Read one entry's bytes into memory. False when it is missing (or there is no open bundle). */
	bool ReadBytes(const FString& InZipPath, TArray<uint8>& OutBytes) const;

	/** Read one entry out of the open zip and write it under TempDir; returns the on-disk path. */
	bool Extract(const FString& InZipPath, FString& OutDiskPath, FString& OutError) const;

	/**
	 * Label an actor, mark it as this import's, and file it in the outliner. One function, called
	 * for every actor an import creates, so a new actor type cannot be added and forget one of the
	 * three -- and in particular cannot forget the tag, which is what re-import matches on.
	 *
	 * In a World Partition level it also assigns the actor (and a landscape's streaming proxies) to
	 * the Data Layer of the current Family; in a non-partitioned level it does not.
	 *
	 * An actor made from `database` files goes to the order's `ODbL` outliner folder instead, takes the
	 * ODbL tag, and (in World Partition) joins the ODbL Data Layer as well. An actor owing the credit
	 * carries its lines as tags, and an equivalence group as one, so Prepare for handover can act on
	 * the level alone, with no bundle open.
	 */
	void Claim(AActor* Actor, const FString& Label) const;
};

/**
 * One artifact type the importer knows: how its manifest block is read, which payloads it declares
 * for the integrity check, and how it is imported.
 *
 * A seam, not a framework. There is no discovery and no reflection: the handlers are an explicit,
 * ordered list (MantlePlaceArtifacts::MakeAll), and the order is the import order. The existing
 * free-function namespaces (MantlePlaceLandscapeImporter, MantlePlaceDrape, ...) remain the
 * implementations; a handler is the thin adapter that says WHEN they run and what is logged around
 * them. Whatever a handler needs to decide lives in a *Logic unit, as it did before.
 *
 * Instances are made per import (MakeAll), so a handler may hold state between Prepare and Import.
 */
class IMantlePlaceArtifactHandler
{
public:
	virtual ~IMantlePlaceArtifactHandler() = default;

	/** Stable key a Selection names this artifact by. */
	virtual FName Id() const = 0;

	/** What a selection UI shows beside the checkbox, and the noun a log line uses. */
	virtual FText DisplayName() const = 0;

	/**
	 * Read this artifact's block into the manifest. False with OutError filled is a fail-closed
	 * refusal of the whole manifest. A block the bundle does not ship is NOT a failure: leave it
	 * absent and return true.
	 */
	virtual bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& Manifest, FString& OutError) const = 0;

	/** A check that needs other blocks as well, run once every block is parsed. */
	virtual bool CrossCheck(const FMantlePlaceVaultManifest& Manifest, FString& OutError) const { return true; }

	/** True when the bundle ships this artifact. */
	virtual bool IsPresent(const FMantlePlaceVaultManifest& Manifest) const = 0;

	/** The integrity terms: every payload this artifact declares, digest or no digest (see FMantlePlaceIntegrityLogic). */
	virtual void CollectDeclared(const FMantlePlaceVaultManifest& Manifest, TArray<FMantlePlaceDeclaredArtifact>& Out) const = 0;

	/**
	 * The files this artifact's actors are made from, whose stated `odbl_role` decides whether they are
	 * ODbL content (MantlePlaceOdblLogic). Every payload it declares by default; an artifact that is
	 * generated from other artifacts' files (the foliage scatter) names those instead.
	 */
	virtual void CollectSourcePaths(const FMantlePlaceVaultManifest& Manifest, TArray<FString>& Out) const
	{
		TArray<FMantlePlaceDeclaredArtifact> Declared;
		CollectDeclared(Manifest, Declared);
		for (const FMantlePlaceDeclaredArtifact& Artifact : Declared)
		{
			Out.Add(Artifact.Path);
		}
	}

	/**
	 * Artifacts that return the same non-empty name stand for the same thing in the level (the terrain
	 * Landscape and the terrain Mesh). Prepare for handover swaps ODbL content for a Produced Work of
	 * the same group where the level holds one. NAME_None: it has no equivalent.
	 */
	virtual FName EquivalenceGroup() const { return NAME_None; }

	/**
	 * The layer family this artifact's actors belong to, and so the World Partition Data Layer they
	 * are assigned to. Declared by every handler, None for one that spawns no actor of its own.
	 */
	virtual EMantlePlaceLayerFamily LayerFamily() const = 0;

	/** True for the artifacts that ARE a terrain representation, of which an import must produce at least one. */
	virtual bool IsTerrainRepresentation() const { return false; }

	/** Why this artifact cannot be produced from this bundle, as a clause, for the "nothing to import" refusal. */
	virtual void ExplainAbsence(const FMantlePlaceVaultManifest& Manifest, TArray<FString>& OutReasons) const {}

	/**
	 * Work that must happen BEFORE the import's undo transaction opens: a glTF import renames what it
	 * brings, and a rename inside the transaction takes the whole import off the undo stack. Creates
	 * assets only; deletes nothing. Called for included artifacts only.
	 */
	virtual void Prepare(FMantlePlaceImportContext& Context) {}

	/** The import proper, inside the one undo transaction. Called for included artifacts only. */
	virtual void Import(FMantlePlaceImportContext& Context) = 0;

	/** After every artifact's Import: the report that needs what the others left in the context. */
	virtual void Finalize(FMantlePlaceImportContext& Context) {}
};

namespace MantlePlaceArtifacts
{
using FHandlers = TArray<TUniquePtr<IMantlePlaceArtifactHandler>>;

/**
 * The registry: a fresh set of handlers in IMPORT order. Explicit on purpose. The order is a
 * contract -- the drape builds its material before the landscape adopts it, and the log reads in
 * this order -- so adding an artifact is one line in a list a reader can see whole.
 */
FHandlers MakeAll();
}
