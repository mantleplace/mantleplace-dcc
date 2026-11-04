// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImporterLibrary.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceCesiumAvailabilityLogic.h"
#include "MantlePlaceDeliveryLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceHLODs.h"
#include "MantlePlaceHandover.h"
#include "MantlePlaceLocalTileServer.h"
#include "MantlePlaceScopedRestore.h"
#include "MantlePlaceStreamStaging.h"
#include "MantlePlaceSha256.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Engine/DataTable.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "DesktopPlatformModule.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "IDesktopPlatform.h"
#include "Framework/Application/SlateApplication.h"
#include "HAL/FileManager.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformProcess.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "HAL/PlatformFileManager.h"
#include "Landscape.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Misc/FileHelper.h"
#include "Misc/Optional.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "ScopedTransaction.h"
#include "Settings/EditorLoadingSavingSettings.h"
#include "Subsystems/EditorAssetSubsystem.h"

#define LOCTEXT_NAMESPACE "MantlePlaceImporter"

namespace
{
	/**
	 * Extract every zip entry the staging record selects into `TempDir`, preserving the in-zip subpath
	 * (CesiumTerrain/14/5615/11520.terrain -> TempDir/CesiumTerrain/14/5615/11520.terrain). Which
	 * entries those are is `MantlePlaceStreamStaging::SelectsEntry`'s decision, made from manifest
	 * pointers only. Returns the number of files written and adds their size to `OutBytes`. Used to
	 * lay the bundle's Cesium-ready artifacts on disk for the local tile server to host.
	 */
	int32 ExtractSelectedEntries(
		const FZipArchiveReader& Reader, const MantlePlaceStreamStaging::FRecord& Selection,
		const FString& TempDir, int64& OutBytes)
	{
		int32 Count = 0;
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		for (const FString& Name : Reader.GetFileNames())
		{
			if (!MantlePlaceStreamStaging::SelectsEntry(Selection, Name))
			{
				continue;
			}
			TArray<uint8> Bytes;
			if (!Reader.TryReadFile(Name, Bytes))
			{
				continue;
			}
			const FString OutPath = FPaths::Combine(TempDir, Name);
			PlatformFile.CreateDirectoryTree(*FPaths::GetPath(OutPath));
			if (FFileHelper::SaveArrayToFile(Bytes, *OutPath))
			{
				++Count;
				OutBytes += Bytes.Num();
			}
		}
		return Count;
	}

	/**
	 * Correct a quantized-mesh layer.json `available` array to list exactly the tiles present on disk,
	 * and say in the log whether it needed correcting at all.
	 *
	 * ETL bundles ship a layer.json whose low-zoom `available` rectangles declare the whole-world
	 * pyramid (e.g. level 1 claims x[0..3] y[0..1] = 8 tiles) while only the single AOI-ancestor tile
	 * per level is actually written. Cesium for Unreal trusts `available`, requests the
	 * declared-but-absent siblings, gets 404s, and aborts with "Errors loading quantized mesh terrain"
	 * — nothing renders. The web app compensates by rewriting `available`; this plugin's
	 * self-contained local server must do the same. We emit one inclusive 1x1 rectangle per present
	 * tile, grouped by zoom. The present tiles form a connected descent chain to the AOI (every tile's
	 * parent exists), so refinement still works.
	 *
	 * This is a workaround for an upstream packaging defect — the bundle's own layer.json is wrong —
	 * and upstream is the fix's home. Which raises the question this function now answers rather than
	 * assumes: **is the defect still there?** It cannot be answered from the source, only from a
	 * bundle, so the answer is logged on every stream. A bundle whose availability already matches its
	 * tiles is written out loud as such and nothing is rewritten: that log line is the evidence this
	 * code can be deleted. Consistency is judged by tile set, so a multi-tile rectangle whose tiles are
	 * all on disk is consistent. A bundle that does not match reports how many declared tiles are
	 * absent and how many present ones are undeclared, which is the evidence an upstream report needs
	 * — "7 of them absent" is a bug report, "availability is wrong" is not.
	 *
	 * Best-effort in both directions: on any failure the original file is left untouched and streaming
	 * proceeds (Cesium will 404 the siblings as before). Returns true if the file was rewritten.
	 */
	bool RewriteCesiumTerrainAvailability(const FString& LayerJsonPath, const FString& JobId)
	{
		FString JsonText;
		if (!FFileHelper::LoadFileToString(JsonText, *LayerJsonPath))
		{
			return false;
		}
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> JsonReader = TJsonReaderFactory<>::Create(JsonText);
		if (!FJsonSerializer::Deserialize(JsonReader, Root) || !Root.IsValid())
		{
			return false;
		}

		// Collect present tiles: <TerrainDir>/{z}/{x}/{y}.terrain.
		const FString TerrainDir = FPaths::GetPath(LayerJsonPath);
		TArray<FString> TileFiles;
		IFileManager::Get().FindFilesRecursive(TileFiles, *TerrainDir, TEXT("*.terrain"), /*Files*/ true, /*Dirs*/ false);

		TArray<FMantlePlaceCesiumTile> Tiles;
		Tiles.Reserve(TileFiles.Num());
		for (const FString& TilePath : TileFiles)
		{
			FMantlePlaceCesiumTile Tile;
			if (FMantlePlaceCesiumAvailabilityLogic::ParseTilePath(TilePath, Tile))
			{
				Tiles.Add(Tile);
			}
		}
		if (Tiles.Num() == 0)
		{
			return false;
		}

		FMantlePlaceCesiumAvailabilityDiff Diff;
		if (!FMantlePlaceCesiumAvailabilityLogic::CorrectAvailability(Root, Tiles, Diff))
		{
			// The signal that this workaround has outlived the defect. If it appears for every bundle
			// a user streams, delete this function and its call — that is what item 3 of the
			// hardening batch asked to be verified, and this is the verification.
			UE_LOG(LogMantlePlaceImport, Log,
				TEXT("Cesium terrain availability for job %s already lists exactly the %d tiles "
					 "present; no rewrite was needed. If this holds for every bundle, the "
					 "availability workaround can be removed."),
				*JobId, Tiles.Num());
			return false;
		}

		// The upstream evidence, with the numbers in it.
		UE_LOG(LogMantlePlaceImport, Warning, TEXT("%s"),
			*FMantlePlaceCesiumAvailabilityLogic::DescribeMismatch(JobId, Diff, Tiles.Num()));

		FString OutText;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutText);
		if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
		{
			return false;
		}
		return FFileHelper::SaveStringToFile(OutText, *LayerJsonPath);
	}

	// The local Cesium stream server outlives StreamBundleIntoCesium so Cesium keeps fetching tiles.
	// Editor-session lifetime; restarted per stream, stopped by StopBundleStream. The server dtor's
	// Stop() touches only the router it already holds a shared reference to — never the module — so
	// static teardown after the module unloads is safe.
	TUniquePtr<FMantlePlaceLocalTileServer> GBundleStreamServer;
}

bool UMantlePlaceImporterLibrary::ReadManifestSha256(const FString& ZipPath, FString& OutSha256, FString& OutError)
{
	OutSha256.Reset();
	OutError.Reset();

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		OutError = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return false;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		OutError = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return false;
	}
	FZipArchiveReader Reader(Handle); // takes ownership of the handle
	if (!Reader.IsValid())
	{
		OutError = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return false;
	}

	TArray<uint8> ManifestBytes;
	if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		OutError = TEXT("Bundle has no Metadata/manifest.json.");
		return false;
	}

	OutSha256 = MantlePlaceSha256::HexDigest(ManifestBytes);
	return true;
}

bool UMantlePlaceImporterLibrary::ReadVaultManifest(
	const FString& ZipPath, FMantlePlaceVaultManifest& OutManifest, FString& OutError)
{
	OutManifest = FMantlePlaceVaultManifest();
	OutError.Reset();

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		OutError = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return false;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		OutError = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return false;
	}
	FZipArchiveReader Reader(Handle); // takes ownership of the handle
	if (!Reader.IsValid())
	{
		OutError = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return false;
	}

	TArray<uint8> ManifestBytes;
	if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		OutError = TEXT("Bundle has no Metadata/manifest.json.");
		return false;
	}

	FString ManifestText;
	FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());

	// The read succeeded; OutManifest.bValid + OutError carry the completeness verdict (an incomplete
	// base_on_demand bundle parses fine but leaves bValid=false with guidance + a populated OrderId).
	OutManifest = MantlePlaceImportManifest::Parse(ManifestText, OutError);
	return true;
}

namespace
{
	/**
	 * Keeps the Content Browser out of the shot for the duration of an import.
	 *
	 * UAssetToolsImpl::ImportAssetTasks already asks for no browser sync -- it hard-codes
	 * `Params.bSyncToBrowser = false` -- but the Interchange completion path does not honour the
	 * request. It resolves the flag as `Var ? Var->GetBool() : bSyncToBrowser` (AssetTools.cpp), so
	 * the CVar wins over the caller and a fully automated import still pops a Content Browser over
	 * the viewport, once per imported file. A vault bundle imports six or more.
	 *
	 * RAII, and deliberately not the plain set/reset pair used for the SCC checkout override above:
	 * leaving this flag off would silently change the editor for the rest of the session, and
	 * ImportVaultPackage returns from many places.
	 */
	struct FScopedNoContentBrowserSync
	{
		IConsoleVariable* const Var;
		const bool bPrevious;

		FScopedNoContentBrowserSync()
			: Var(IConsoleManager::Get().FindConsoleVariable(
				TEXT("Interchange.FeatureFlags.Import.SyncToBrowser")))
			, bPrevious(Var != nullptr && Var->GetBool())
		{
			if (Var != nullptr)
			{
				Var->Set(false, ECVF_SetByCode);
			}
		}

		~FScopedNoContentBrowserSync()
		{
			if (Var != nullptr)
			{
				Var->Set(bPrevious, ECVF_SetByCode);
			}
		}
	};
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackage(
	const FString& ZipPath,
	EMantlePlaceImportMode Mode)
{
	// The foliage scatter is opt-in, by the project setting: generated content the bundle did not ship.
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return ImportVaultPackageWithSelection(ZipPath,
		FMantlePlaceImportSelection::FromMode(Mode, Settings != nullptr && Settings->bGenerateFoliageScatter));
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackageWithSelection(
	const FString& ZipPath,
	const FMantlePlaceImportSelection& Selection)
{
	FMantlePlaceImportResult Result;
	TArray<FString> Log;

	// Timing runs for the WHOLE function, including every early return. An import that refuses at
	// the integrity check is still an import somebody waited on, and "it failed after four minutes"
	// is a different report from "it failed instantly" — so the summary is emitted by the scope
	// guard below rather than at the success path, which would only ever measure the happy case.
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	MantlePlaceImportTiming::FScopedCurrent PublishTimeline(Timeline);
	FMantlePlaceScopedRestore EmitTiming([&Timeline, &Result, &ZipPath]()
	{
		for (const FString& Line : Timeline.BuildSummary())
		{
			UE_LOG(LogMantlePlaceImport, Log, TEXT("%s"), *Line);
		}

		// And the same timeline for a reader that is not a person, when something has said where to
		// put it. Emitted here rather than on the success path for the reason above: the run that
		// most needs measuring is the one that failed.
		const FString RecordDirectory = MantlePlaceImportTiming::RecordDirectoryFromEnvironment();
		if (RecordDirectory.IsEmpty())
		{
			return;
		}

		MantlePlaceImportTiming::FRecordContext RecordContext;
		RecordContext.Bundle = FPaths::GetCleanFilename(ZipPath);
		RecordContext.bSucceeded = Result.bSuccess;

		const FString Written = MantlePlaceImportTiming::WriteRecord(Timeline, RecordContext, RecordDirectory);
		if (Written.IsEmpty())
		{
			// A warning, never a failure: a diagnostic that can fail an import is a worse
			// diagnostic than none. Whoever set the variable is the one who will notice the
			// missing file, and it is their gate that should say so.
			UE_LOG(LogMantlePlaceImport, Warning,
			       TEXT("Could not write the import timeline record into %s (%s)"),
			       *RecordDirectory, MantlePlaceImportTiming::RecordDirectoryVariable);
		}
		else
		{
			UE_LOG(LogMantlePlaceImport, Log, TEXT("Import timeline record: %s"), *Written);
		}
	});

	// --- Validate the file ---
	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		Result.Message = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return Result;
	}

	// --- Open the zip (FZipArchiveReader takes ownership of the handle) ---
	// Scoped so the phase closes when the manifest is parsed, whichever way this block exits.
	TOptional<MantlePlaceImportTiming::FScopedPhase> ZipPhase;
	ZipPhase.Emplace(Timeline, MantlePlaceImportTiming::Phase::ZipAndManifest);

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		Result.Message = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return Result;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		Result.Message = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return Result;
	}

	// --- Read + parse the manifest ---
	TArray<uint8> ManifestBytes;
	if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		Result.Message = TEXT("Bundle has no Metadata/manifest.json.");
		return Result;
	}
	FString ManifestText;
	FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());

	FString ParseError;
	const FMantlePlaceVaultManifest Manifest = MantlePlaceImportManifest::Parse(ManifestText, ParseError);
	Result.JobId = Manifest.JobId;
	if (!Manifest.bValid)
	{
		Result.Message = FString::Printf(TEXT("Manifest error: %s"), *ParseError);
		return Result;
	}

	// Surface the bundle's schema version up front: the importer keys off the manifest's `unreal`
	// block, not this version, but logging it tells the user exactly which ETL output they fed in
	// (a 1.1.0 bundle that omits an optional block looks identical to a 1.0.0 one at the actor
	// level otherwise). Verbatim and unqualified: Parse has already refused everything that is not
	// a semver string, so by here this is always MAJOR.MINOR.PATCH.
	Log.Add(FString::Printf(TEXT("Bundle manifest version %s (jobId %s)."),
		*Manifest.Version, *MantlePlaceImportNaming::ShortIdentity(Manifest.JobId)));

	// What the bundle is delivered in and, on an imperial order, that this host's content is not.
	// Read off the host-neutral `delivery` block for display only; nothing below places from it.
	Log.Append(FMantlePlaceDeliveryLogic::DescribeDelivery(Manifest.Delivery));

	ZipPhase.Reset();

	// --- Verify all: the downloaded bytes must match the manifest's declared sha256 before anything
	// is imported. A corrupt/truncated/tampered download aborts here, creating nothing.
	FString IntegrityError;
	if (!MantlePlaceImportOrchestration::VerifyAll(Manifest, Reader, Timeline, IntegrityError, &Log))
	{
		Result.Message = FString::Printf(TEXT("Integrity check failed: %s"), *IntegrityError);
		return Result;
	}

	UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		Result.Message = TEXT("No editor world is open to import into.");
		return Result;
	}

	// The identity everything below is keyed on: the ORDER when the bundle names one, and
	// otherwise a content hash of the manifest. NEVER the job id — the manifest documents it as
	// changing on every rebuild, so keying on it made re-materialising an order create a second
	// folder and a second landscape while the wipe below looked at a path nobody used (ADR 0002).
	// The manifest bytes are already in memory from the parse above, so the hash costs no I/O.
	const FString Identity = MantlePlaceImportNaming::ResolveIdentity(
		Manifest.OrderId, MantlePlaceSha256::HexDigest(ManifestBytes));
	if (!MantlePlaceImportNaming::IsUsableIdentity(Identity))
	{
		// Refused, not defaulted. The identity becomes a path segment of a directory this function
		// FORCE-DELETES; an empty one collapses that path onto the content root, where the delete
		// would reach every import in the project.
		Result.Message = TEXT(
			"This bundle carries neither a usable order id nor a hashable manifest, so there is no "
			"safe name for its content folder. Nothing has been imported.");
		return Result;
	}

	const FString ContentRoot = UMantlePlaceEditorSettings::ResolveGeneratedContentRoot();
	const FString DestPackagePath = MantlePlaceImportNaming::ImportRoot(ContentRoot, Identity);
	Result.Identity = Identity;
	Result.ContentPath = DestPackagePath;

	// Decide up-front whether anything requested can be produced. If NOTHING can be (e.g. a Mesh
	// import of a Cesium-terrain-only v8 bundle that ships no Terrain.glb), bail BEFORE the destructive
	// idempotent wipe below — otherwise we would force-delete a prior import's still-referenced
	// drape assets (greying out an already-imported Landscape) only to produce nothing.
	const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
	const FString NothingToImport =
		MantlePlaceImportOrchestration::ExplainNothingToImport(Handlers, Manifest, Selection);
	if (!NothingToImport.IsEmpty())
	{
		Result.Message = NothingToImport;
		return Result;
	}

	// Keyed on the identity rather than the raw job id, which is an unvalidated string out of
	// bundle JSON being used as a directory name.
	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ImportTmp")
		/ MantlePlaceImportNaming::ShortIdentity(Identity);
	PlatformFile.CreateDirectoryTree(*TempDir);

	// Nothing this import creates should steal the viewport. See FScopedNoContentBrowserSync.
	const FScopedNoContentBrowserSync NoBrowserSync;

	// These are freshly generated assets, not yet in source control. Suppress the editor's
	// auto-checkout-on-modify for the duration of the import so renames/edits don't spam the
	// connected SCC provider with checkouts of files that aren't under source control.
	//
	// The override is EDITOR-WIDE and outlives this function, so restoring it is not optional and is
	// not this function's to forget. The guard makes "restored on every exit path" a property of the
	// scope instead of a thing to remember; see FMantlePlaceScopedRestore and
	// MantlePlace.Import.ScopedRestore.
	UEditorLoadingSavingSettings* LoadSaveSettings = GetMutableDefault<UEditorLoadingSavingSettings>();
	LoadSaveSettings->SetAutomaticallyCheckoutOnAssetModificationOverride(false);
	const FMantlePlaceScopedRestore RestoreAutoCheckout(
		[LoadSaveSettings] { LoadSaveSettings->ResetAutomaticallyCheckoutOnAssetModificationOverride(); });

	// --- World Partition: a prior import's actors in unloaded regions are loaded for the wipe. ---
	// Held until this function returns, so released after the transaction commits: the wipe destroys
	// what it loaded, and whatever else loaded with them unloads again. One that cannot be loaded
	// refuses the import, before either wipe and before the transaction, and changes nothing.
	MantlePlaceImportOrchestration::FPriorActorLoad PriorActorLoad;
	const FString UnloadedRefusal = MantlePlaceImportOrchestration::LoadUnloadedPriorActors(World, Identity, PriorActorLoad);
	if (!UnloadedRefusal.IsEmpty())
	{
		Result.Message = UnloadedRefusal;
		return Result;
	}
	if (PriorActorLoad.Loaded > 0)
	{
		Log.Add(FString::Printf(
			TEXT("Loaded %d actor(s) of the previous import from unloaded World Partition regions, to replace them."),
			PriorActorLoad.Loaded));
	}

	// --- Wipe, assets: a prior import of THIS order goes, a different order's content is refused. ---
	const FString Refusal = MantlePlaceImportOrchestration::WipePriorAssets(Identity, DestPackagePath, Timeline);
	if (!Refusal.IsEmpty())
	{
		// Before the transaction opens and before anything is created, so a refusal changes
		// nothing at all -- including the auto-checkout override, which RestoreAutoCheckout
		// puts back on the way out of this return.
		Result.Message = Refusal;
		return Result;
	}

	FMantlePlaceImportContext Context(World, Manifest, &Reader, Selection, Timeline, Result, Log);
	Context.Identity = Identity;
	Context.TempDir = TempDir;
	Context.DestPackagePath = DestPackagePath;
	Context.ContentRoot = ContentRoot;

	// glTF asset imports run BEFORE the transaction opens, and everything after it is either an
	// actor operation or an asset creation that deletes nothing.
	//
	// A .glb brings embedded textures and materials with it, named after the source (Terrain_texture_0),
	// and naming those to the project standard means a rename. Renaming a freshly-imported asset
	// inside the import's own transaction takes the whole import off the undo stack -- ObjectTools
	// resets the entire buffer when the object it is deleting is referenced only by that buffer
	// (see MantlePlaceImportNaming::ImportNameFor). Measured 2026-08-30 on a Both-mode import:
	// Terrain.glb's texture rename purged the buffer and Ctrl+Z then moved nothing at all.
	//
	// The one thing that must still precede this is the asset wipe above: it force-deletes a prior
	// import's assets, and doing it AFTER would delete what we just imported.
	MantlePlaceImportOrchestration::RunPrepare(Handlers, Context);

	FScopedTransaction Transaction(LOCTEXT("ImportVaultPackage", "Import Mantle Place Vault Package"));

	// ⛔ Declared immediately AFTER the transaction so it is destroyed immediately BEFORE it —
	// locals tear down in reverse declaration order, which makes this scope end exactly where the
	// transaction's own commit begins. Declared before it, this would close first and measure
	// nothing.
	//
	// It is an ENCLOSING phase, and the summary prints it as one: it is a function-scope local, so
	// it spans every artifact imported below rather than the gap before the commit. The commit
	// itself is still unmeasured — sizing it needs a guard inside FScopedTransaction, which is
	// engine code — so the row is named for the span it does cover.
	const MantlePlaceImportTiming::FScopedPhase TransactionPhase(
		Timeline, MantlePlaceImportTiming::Phase::InsideTransaction);

	// --- Wipe, actors: re-importing replaces them instead of stacking duplicate (coincident) actors. ---
	// A prior scatter's emptied PCG partition actors are kept for this import's scatter to generate on,
	// and released once it has (ReplacePriorActors says why).
	TArray<TWeakObjectPtr<AActor>> KeptPartitionActors;
	const int32 Replaced = MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline, &KeptPartitionActors);
	if (Replaced > 0)
	{
		Log.Add(FString::Printf(TEXT("Replaced %d actor(s) from a prior import of this bundle."), Replaced));
	}

	// --- Import: every included artifact, in registry order; then the report that needs them all. ---
	MantlePlaceImportOrchestration::RunImport(Handlers, Context);
	MantlePlaceImportOrchestration::RunFinalize(Handlers, Context);
	MantlePlaceImportOrchestration::ReleaseKeptPartitionActors(World, KeptPartitionActors);

	// --- HLODs: configured by Claim, never built here (MantlePlaceHLODLogic says why); say how. ---
	const FString HLODNote = MantlePlaceHLODLogic::PostImportNote(
		UWorld::IsPartitionedWorld(World), MantlePlaceHLODs::CountAssigned(World, ContentRoot));
	if (!HLODNote.IsEmpty())
	{
		Log.Add(HLODNote);
	}

	// --- Report ---
	Result.bSuccess = Context.bAllRequestedSucceeded && Result.CreatedActors.Num() > 0;
	if (Result.bSuccess)
	{
		// Said in the message the user reads, not only in this file's comments: nothing this
		// importer creates is saved -- there is no SavePackage call in the plugin, every generated
		// package is only marked dirty -- so a user who closes the editor now loses the import with
		// no further warning. Last line on purpose, so it is the one left on screen.
		Log.Add(TEXT("Nothing is saved yet: the imported assets and your level stay unsaved until you use File > Save All (Ctrl+Shift+S)."));
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Import complete: %d actor(s) created. Nothing is saved yet: the imported assets and "
				 "your level stay unsaved until you use File > Save All (Ctrl+Shift+S)."),
			Result.CreatedActors.Num());
	}
	Result.Message = FString::Join(Log, TEXT("\n"));

	// Record what this folder is, so the next import of a DIFFERENT order that happens to share
	// these eight characters is refused instead of force-deleting this one. Written only on
	// success: a record for content that was never created would refuse a later import of the
	// order that legitimately owns the folder.
	if (Result.bSuccess)
	{
		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		Record.ContentRoot = ContentRoot;
		Record.JobId = Manifest.JobId;
		// The manifest's own top-level `bbox` (WGS84 degrees), carried as stated so Open in curator can
		// open the web curator on this order's extent later. Copied, never computed.
		if (Manifest.Cesium.bHasBbox)
		{
			Record.Extent.West = Manifest.Cesium.BboxWestDeg;
			Record.Extent.South = Manifest.Cesium.BboxSouthDeg;
			Record.Extent.East = Manifest.Cesium.BboxEastDeg;
			Record.Extent.North = Manifest.Cesium.BboxNorthDeg;
			Record.bHasExtent = Record.Extent.IsValid();
		}
		if (!MantlePlaceImportProvenance::Write(Record))
		{
			// Non-fatal, and said out loud rather than swallowed: the import is complete and
			// correct, but without the record a later import that collides on the short identity
			// refuses rather than replaces, and this content then looks like legacy content.
			UE_LOG(LogMantlePlaceImport, Warning,
				TEXT("Import succeeded but its provenance record could not be written to %s. A "
					 "future re-import of this order will refuse rather than replace it."),
				*MantlePlaceImportProvenance::RecordPath(Identity));
		}
	}
	return Result;
}

bool UMantlePlaceImporterLibrary::BrowseForVaultZip(FString& OutZipPath)
{
	IDesktopPlatform* DesktopPlatform = FDesktopPlatformModule::Get();
	if (DesktopPlatform == nullptr)
	{
		return false;
	}

	// Parent the dialog to the editor's best top-level window so it is modal to the editor.
	const void* ParentWindowHandle = FSlateApplication::Get().FindBestParentWindowHandleForDialogs(nullptr);

	// Open where bundles are typically downloaded; the user can navigate from there.
	const FString DefaultPath = FPlatformProcess::UserDir();

	TArray<FString> OutFiles;
	const bool bPicked = DesktopPlatform->OpenFileDialog(
		ParentWindowHandle,
		TEXT("Select Mantle Place vault bundle"),
		DefaultPath,
		TEXT(""),
		TEXT("Vault bundle (*.zip)|*.zip"),
		EFileDialogFlags::None,
		OutFiles);

	if (bPicked && OutFiles.Num() > 0)
	{
		OutZipPath = FPaths::ConvertRelativePathToFull(OutFiles[0]);
		return true;
	}
	return false;
}

FMantlePlaceStreamInfo UMantlePlaceImporterLibrary::StreamBundleIntoCesium(const FString& ZipPath)
{
	FMantlePlaceStreamInfo Info;

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		Info.Message = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return Info;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		Info.Message = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return Info;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		Info.Message = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return Info;
	}

	TArray<uint8> ManifestBytes;
	if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		Info.Message = TEXT("Bundle has no Metadata/manifest.json.");
		return Info;
	}
	FString ManifestText;
	FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());
	FString ParseError;
	const FMantlePlaceVaultManifest Manifest = MantlePlaceImportManifest::Parse(ManifestText, ParseError);
	Info.JobId = Manifest.JobId;

	if (!Manifest.Cesium.bHasTerrain)
	{
		Info.Message = TEXT("This bundle ships no Cesium quantized-mesh terrain (layout.cesiumTerrain is "
			"absent). Re-export from a v8+ pipeline that produces Cesium terrain tiles.");
		return Info;
	}

	// Zip-entry prefix derived from the manifest so this keeps working as the bundle layout renames
	// the terrain folder across versions (v13+: CesiumTerrain/; legacy: Terrain/).
	const FString TerrainPrefix = MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(Manifest.Cesium.TerrainPath);
	if (TerrainPrefix.IsEmpty())
	{
		Info.Message = TEXT("Bundle's layout.cesiumTerrain has no directory component; refusing to "
			"extract (would match every zip entry).");
		return Info;
	}

	// Keyed on the identity, not the raw job id: a directory name out of unvalidated bundle JSON.
	// Refused when neither the order nor the manifest digest yields a usable one, for the same reason
	// the native import refuses it — this path creates and clears a directory named by it.
	const FString ManifestSha256 = MantlePlaceSha256::HexDigest(ManifestBytes);
	const FString Identity =
		MantlePlaceImportNaming::ResolveIdentity(Manifest.OrderId, ManifestSha256);
	if (!MantlePlaceImportNaming::IsUsableIdentity(Identity))
	{
		Info.Message = TEXT("Bundle carries neither a usable order id nor a readable manifest digest, "
			"so there is no safe name for its staging directory. Nothing was extracted.");
		return Info;
	}

	// Lay the bundle's Cesium-ready artifacts on disk for the local tile server to host — once per
	// bundle rather than once per stream. Extracting every tile and rescanning the whole tile tree on
	// each "Stream into Cesium" of the SAME bundle is work with no output, and the rescan is the
	// expensive half. The record beside the directory says which bundle is staged there; anything
	// that does not match exactly re-stages, because this directory is scratch and rebuilding it
	// costs an extraction while serving a stale one serves the wrong terrain.
	const FString StageDir = MantlePlaceStreamStaging::StagingDir(Identity);
	const FString TerrainRootOnDisk = FPaths::Combine(StageDir, Manifest.Cesium.TerrainPath);

	MantlePlaceStreamStaging::FRecord Incoming;
	Incoming.Identity = Identity;
	Incoming.ManifestSha256 = ManifestSha256;
	Incoming.TerrainPrefix = TerrainPrefix;
	Incoming.CesiumTerrainPath = Manifest.Cesium.TerrainPath;
	// The one imagery file this stream serves, by the same pointer and the same condition that build
	// Info.ImageryUrl below — the only imagery the Python helper reads. Empty when the stream serves
	// none, and then none is extracted.
	Incoming.DrapePath = (Manifest.Drape.bPresent && !Manifest.Drape.Path.IsEmpty()) ? Manifest.Drape.Path : FString();
	Incoming.SchemeVersion = MantlePlaceStreamStaging::CurrentSchemeVersion;

	MantlePlaceStreamStaging::FRecord Staged;
	const bool bHasRecord = MantlePlaceStreamStaging::Read(Identity, Staged);
	const MantlePlaceStreamStaging::EVerdict Verdict = MantlePlaceStreamStaging::Classify(
		bHasRecord, Staged, Incoming, PlatformFile.FileExists(*TerrainRootOnDisk));

	if (Verdict == MantlePlaceStreamStaging::EVerdict::Stage)
	{
		// Cleared, not merged into. A rebuild of the same order lands in the same directory, and the
		// availability rewrite declares every .terrain file it finds beneath it — so a previous
		// build's leftover tiles would be declared available and served as this build's.
		PlatformFile.DeleteDirectoryRecursively(*StageDir);
		PlatformFile.CreateDirectoryTree(*StageDir);

		// Every extracted entry is named by a manifest pointer: the terrain subtree under
		// layout.cesium_terrain's directory, and the drape the stream serves (HPS-32).
		int64 StagedBytes = 0;
		Incoming.EntryCount = ExtractSelectedEntries(Reader, Incoming, StageDir, StagedBytes);
		if (Incoming.EntryCount == 0)
		{
			Info.Message = FString::Printf(TEXT("Bundle declares Cesium terrain but no %s entries were extracted."), *TerrainPrefix);
			return Info;
		}
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Staged %d entries (%lld bytes) for this bundle at %s."),
			Incoming.EntryCount, StagedBytes, *StageDir);

		// Correct the bundle's over-declared `available` so Cesium only requests tiles that exist,
		// and log whether it needed correcting — see RewriteCesiumTerrainAvailability.
		//
		// ⛔ Hotspot: a recursive scan of the extracted terrain tree plus a JSON rewrite, once per
		// stream. The detail carries the entry count, because the cost is a function of it and a
		// timing line without it is not comparable between two differently sized bundles.
		//
		// FScopedCurrentPhase, not FScopedPhase: this is a different function from
		// ImportVaultPackage and there is no timeline in scope here. The first cut used the
		// in-scope form and did not compile — which is the one thing the private compile gate
		// catches that neither review nor this repository's own CI can.
		{
			const MantlePlaceImportTiming::FScopedCurrentPhase AvailabilityPhase(
				MantlePlaceImportTiming::Phase::CesiumAvailability,
				FString::Printf(TEXT("%d entries"), Incoming.EntryCount));
			RewriteCesiumTerrainAvailability(TerrainRootOnDisk, Manifest.JobId);
		}

		if (!MantlePlaceStreamStaging::Write(Incoming))
		{
			// Non-fatal, and said out loud rather than swallowed: this stream is complete and
			// correct, and the only cost is that the next stream of this bundle stages it again.
			UE_LOG(LogMantlePlaceImport, Warning,
				TEXT("Streaming staged correctly but its record could not be written to %s; the next "
					 "stream of this bundle will extract it again."),
				*MantlePlaceStreamStaging::RecordPath(Identity));
		}
	}
	else
	{
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Reusing the %d entries already staged for this bundle at %s."),
			Staged.EntryCount, *StageDir);
	}

	// Start (or restart) the loopback server rooted at the extracted bundle dir. Try a small port range
	// so a busy default port doesn't block streaming.
	if (!GBundleStreamServer.IsValid())
	{
		GBundleStreamServer = MakeUnique<FMantlePlaceLocalTileServer>();
	}
	// The first port and how many to try are project settings (Project Settings -> Plugins -> Mantle
	// Place); the scan is kept as the fallback it always was. A busy port is a local condition —
	// another editor, another tool — and a stream that gave up on it would have nothing useful to
	// say; the setting is for a whole class of machine with something else on the default.
	int32 FirstPort = 0;
	int32 PortCount = 0;
	UMantlePlaceEditorSettings::ResolveLocalTileServerPortScan(FirstPort, PortCount);

	FString BaseUrl, ServerError;
	for (int32 Offset = 0; Offset < PortCount && BaseUrl.IsEmpty(); ++Offset)
	{
		BaseUrl = GBundleStreamServer->Start(StageDir, static_cast<uint32>(FirstPort + Offset), ServerError);
	}
	if (BaseUrl.IsEmpty())
	{
		// Naming the range makes the next step obvious: it is a setting, and the user can move it.
		Info.Message = FString::Printf(
			TEXT("Failed to start local tile server on any port from %d to %d: %s. Change the first "
				 "port or the scan count under Project Settings -> Plugins -> Mantle Place."),
			FirstPort, FirstPort + PortCount - 1, *ServerError);
		return Info;
	}

	Info.bSuccess = true;
	Info.BaseUrl = BaseUrl;
	Info.CesiumTerrainUrl = BaseUrl / Manifest.Cesium.TerrainPath;
	if (!Incoming.DrapePath.IsEmpty())
	{
		Info.ImageryUrl = BaseUrl / Incoming.DrapePath;
	}
	Info.bHasBbox = Manifest.Cesium.bHasBbox;
	Info.BboxWestDeg = Manifest.Cesium.BboxWestDeg;
	Info.BboxSouthDeg = Manifest.Cesium.BboxSouthDeg;
	Info.BboxEastDeg = Manifest.Cesium.BboxEastDeg;
	Info.BboxNorthDeg = Manifest.Cesium.BboxNorthDeg;
	Info.Message = FString::Printf(
		TEXT("Streaming bundle %s on %s (%d Cesium terrain tiles). Cesium3DTileset Url -> %s"),
		*MantlePlaceImportNaming::ShortIdentity(Manifest.JobId), *BaseUrl, Manifest.Cesium.TerrainTileCount, *Info.CesiumTerrainUrl);
	return Info;
}

void UMantlePlaceImporterLibrary::StopBundleStream()
{
	if (GBundleStreamServer.IsValid())
	{
		GBundleStreamServer->Stop();
	}
}

FString UMantlePlaceImporterLibrary::BuildHLODs()
{
	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	return MantlePlaceHLODs::Build(World).Message;
}

FString UMantlePlaceImporterLibrary::PrepareForHandover()
{
	return MantlePlaceHandover::PrepareEditorWorld().Summary();
}

#undef LOCTEXT_NAMESPACE
