// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImporterLibrary.h"

#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceCesiumAvailabilityLogic.h"
#include "MantlePlaceCoverageRasters.h"
#include "MantlePlaceDrape.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceLandscapeImporter.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceLocalTileServer.h"
#include "MantlePlaceMeshImporter.h"
#include "MantlePlaceRoadSplinesLogic.h"
#include "MantlePlaceScopedRestore.h"
#include "MantlePlaceStreamStaging.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceTreePointsLogic.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow

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
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "ScopedTransaction.h"
#include "Settings/EditorLoadingSavingSettings.h"
#include "Subsystems/EditorAssetSubsystem.h"

#define LOCTEXT_NAMESPACE "MantlePlaceImporter"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceImport, Log, All);

namespace
{
	/** Read one entry out of the open zip and write it under TempDir; returns the on-disk path. */
	bool ExtractEntry(
		const FZipArchiveReader& Reader,
		const FString& InZipPath,
		const FString& TempDir,
		FString& OutDiskPath,
		FString& OutError)
	{
		TArray<uint8> Bytes;
		if (!Reader.TryReadFile(InZipPath, Bytes))
		{
			OutError = FString::Printf(TEXT("Bundle is missing expected entry: %s"), *InZipPath);
			return false;
		}

		OutDiskPath = TempDir / FPaths::GetCleanFilename(InZipPath);
		if (!FFileHelper::SaveArrayToFile(Bytes, *OutDiskPath))
		{
			OutError = FString::Printf(TEXT("Could not write temp file: %s"), *OutDiskPath);
			return false;
		}
		return true;
	}

	/**
	 * Fail-closed integrity check: hash one zip entry's bytes and compare to the manifest's declared
	 * sha256. Returns true (skip) when ExpectedHex is empty (legacy bundle that predates the hash); on a
	 * read failure or a mismatch it fills OutError and returns false so the caller can abort the import.
	 */
	bool VerifyEntrySha256(
		const FZipArchiveReader& Reader,
		const FString& InZipPath,
		const FString& ExpectedHex,
		FString& OutError)
	{
		if (ExpectedHex.IsEmpty())
		{
			return true; // nothing declared to verify against
		}
		TArray<uint8> Bytes;
		if (!Reader.TryReadFile(InZipPath, Bytes))
		{
			OutError = FString::Printf(TEXT("Integrity check could not read entry: %s"), *InZipPath);
			return false;
		}
		const FString Actual = MantlePlaceSha256::HexDigest(Bytes);
		if (!Actual.Equals(ExpectedHex, ESearchCase::IgnoreCase))
		{
			OutError = FString::Printf(
				TEXT("Integrity check failed for %s: manifest sha256 %s != computed %s."),
				*InZipPath, *ExpectedHex, *Actual);
			return false;
		}
		return true;
	}

	/**
	 * Extract every zip entry whose path begins with one of `Prefixes` into `TempDir`, preserving the
	 * in-zip subpath (Terrain/14/5615/11520.terrain -> TempDir/Terrain/14/5615/11520.terrain). Returns
	 * the number of files written. Used to lay the bundle's Cesium-ready artifacts on disk for the local
	 * tile server to host.
	 */
	int32 ExtractSubtree(const FZipArchiveReader& Reader, const TArray<FString>& Prefixes, const FString& TempDir)
	{
		int32 Count = 0;
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		for (const FString& Name : Reader.GetFileNames())
		{
			if (Name.EndsWith(TEXT("/")))
			{
				continue; // directory entry
			}
			bool bWanted = false;
			for (const FString& Prefix : Prefixes)
			{
				if (Name.StartsWith(Prefix))
				{
					bWanted = true;
					break;
				}
			}
			if (!bWanted)
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
	 * code can be deleted. A bundle that does not match reports the two counts, which is the evidence
	 * an upstream report needs — "declares 8, ships 1" is a bug report, "availability is wrong" is not.
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

		int64 DeclaredTileCount = 0;
		if (FMantlePlaceCesiumAvailabilityLogic::IsAvailabilityConsistent(Root, Tiles, DeclaredTileCount))
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
		UE_LOG(LogMantlePlaceImport, Warning,
			TEXT("Cesium terrain layer.json for job %s declares %lld tiles but the bundle ships %d. "
				 "Rewriting availability so Cesium does not 404 its way to a load error. This is an "
				 "upstream packaging defect in the bundle, not an import fault — report it with this "
				 "line."),
			*JobId, DeclaredTileCount, Tiles.Num());

		Root->SetArrayField(TEXT("available"), FMantlePlaceCesiumAvailabilityLogic::BuildAvailability(Tiles));

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

namespace
{
	/**
	 * Label an actor, mark it as this import's, and file it in the outliner. One function, called
	 * for every actor an import creates, so a new actor type cannot be added and forget one of the
	 * three -- and in particular cannot forget the tag, which is what re-import matches on.
	 */
	void ClaimImportedActor(AActor* Actor, const FString& Identity, const FString& Label)
	{
		if (Actor == nullptr)
		{
			return;
		}
		Actor->SetActorLabel(Label);
		Actor->Tags.AddUnique(FName(*MantlePlaceImportNaming::ImportTag(Identity)));
		Actor->SetFolderPath(FName(*MantlePlaceImportNaming::OutlinerFolder(Identity)));
	}
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackage(
	const FString& ZipPath,
	EMantlePlaceImportMode Mode)
{
	FMantlePlaceImportResult Result;
	TArray<FString> Log;

	// --- Validate the file ---
	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		Result.Message = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return Result;
	}

	// --- Open the zip (FZipArchiveReader takes ownership of the handle) ---
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

	// --- Fail-closed integrity check: the downloaded bytes must match the manifest's declared sha256
	// before anything is imported. A corrupt/truncated/tampered download aborts here, creating nothing.
	// Exception: the tree-points CSV has no manifest pointer and therefore no declared sha256 — it
	// imports unverified until the platform ships landcover pointer blocks.
	{
		FString IntegrityError;
		if ((Manifest.bHasHeightmap && !VerifyEntrySha256(Reader, Manifest.HeightmapPath, Manifest.HeightmapSha256, IntegrityError)) || (Manifest.bHasDrape && !VerifyEntrySha256(Reader, Manifest.DrapePath, Manifest.DrapeSha256, IntegrityError)) || (Manifest.bHasMesh && !VerifyEntrySha256(Reader, Manifest.MeshPath, Manifest.MeshSha256, IntegrityError)) || (Manifest.bHasBuildings && !VerifyEntrySha256(Reader, Manifest.BuildingsPath, Manifest.BuildingsSha256, IntegrityError)) || (Manifest.bHasRoadSplines && !VerifyEntrySha256(Reader, Manifest.RoadSplinesPath, Manifest.RoadSplinesSha256, IntegrityError)))
		{
			Result.Message = FString::Printf(TEXT("Integrity check failed: %s"), *IntegrityError);
			return Result;
		}

		// The landscape layers are a loop rather than another term in the chain above: there are up
		// to eight of them, each with its own source raster and its UE-ready companions. Every
		// declared hash is checked, including the seven layers this importer parses but does not yet
		// apply — the check is on the bundle's bytes, not on what today's importer happens to read,
		// and the schema states outright that these are verified fail-closed before any actor spawns.
		for (const FMantlePlaceLandscapeLayer& Layer : Manifest.LandscapeLayers)
		{
			if (!VerifyEntrySha256(Reader, Layer.Path, Layer.Sha256, IntegrityError))
			{
				Result.Message = FString::Printf(TEXT("Integrity check failed: %s"), *IntegrityError);
				return Result;
			}
			for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
			{
				if (!VerifyEntrySha256(Reader, Raster.Path, Raster.Sha256, IntegrityError))
				{
					Result.Message = FString::Printf(TEXT("Integrity check failed: %s"), *IntegrityError);
					return Result;
				}
			}
		}
	}

	// The verification gate is a product claim ("verified before anything is
	// written"), so its PASSING is narrated, not only its failure — log
	// followers should see the gate clear before the first actor spawns.
	UE_LOG(LogMantlePlaceImport, Log,
		TEXT("Integrity verified: every manifest-declared sha256 matches (jobId %s)."),
		*MantlePlaceImportNaming::ShortIdentity(Manifest.JobId));

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

	// Keyed on the identity rather than the raw job id, which is an unvalidated string out of
	// bundle JSON being used as a directory name.
	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ImportTmp")
		/ MantlePlaceImportNaming::ShortIdentity(Identity);
	PlatformFile.CreateDirectoryTree(*TempDir);

	// Decide up-front what each requested representation needs. If NOTHING requested can be produced
	// (e.g. a Mesh import of a Cesium-terrain-only v8 bundle that ships no Terrain.glb), bail BEFORE the
	// destructive idempotent wipe below — otherwise we would force-delete a prior import's still-
	// referenced drape assets (greying out an already-imported Landscape) only to produce nothing.
	const bool bWantLandscape = (Mode == EMantlePlaceImportMode::Landscape || Mode == EMantlePlaceImportMode::Both);
	const bool bWantMesh = (Mode == EMantlePlaceImportMode::Mesh || Mode == EMantlePlaceImportMode::Both);
	const bool bCanLandscape = bWantLandscape && Manifest.bHasHeightmap;
	const bool bCanMesh = bWantMesh && Manifest.bHasMesh;
	if (!bCanLandscape && !bCanMesh)
	{
		TArray<FString> Reasons;
		if (bWantMesh && !Manifest.bHasMesh)
		{
			Reasons.Add(Manifest.MeshAbsentReason.IsEmpty()
				? TEXT("this bundle has no static mesh (Terrain.glb)")
				: FString::Printf(
					TEXT("this bundle has no static mesh (pipeline: %s) — the ETL did not generate a mesh for this AOI"),
					*Manifest.MeshAbsentReason));
		}
		if (bWantLandscape && !Manifest.bHasHeightmap)
		{
			Reasons.Add(TEXT("this bundle has no heightmap"));
		}
		Result.Message = FString::Printf(
			TEXT("Nothing to import: %s. (Existing assets left untouched.) This bundle ships Cesium "
				 "quantized-mesh terrain — use a Landscape import or \"Stream into Cesium\" instead."),
			*FString::Join(Reasons, TEXT("; ")));
		return Result;
	}

	// Nothing this import creates should steal the viewport. See FScopedNoContentBrowserSync.
	const FScopedNoContentBrowserSync NoBrowserSync;

	// These are freshly generated assets, not yet in source control. Suppress the editor's
	// auto-checkout-on-modify for the duration of the import so renames/edits don't spam the
	// connected SCC provider with checkouts of files that aren't under source control.
	//
	// The override is EDITOR-WIDE and outlives this function, so restoring it is not optional and is
	// not this function's to forget. It used to be a Set here and a Reset before each return, which
	// is correct only for the exit paths that existed when it was written -- everything below this
	// line returns from several places, and the next one added would have leaked the override into
	// the rest of the session with nothing to notice it. The guard makes "restored on every exit
	// path" a property of the scope instead of a thing to remember; see FMantlePlaceScopedRestore
	// and MantlePlace.Import.ScopedRestore.
	UEditorLoadingSavingSettings* LoadSaveSettings = GetMutableDefault<UEditorLoadingSavingSettings>();
	LoadSaveSettings->SetAutomaticallyCheckoutOnAssetModificationOverride(false);
	const FMantlePlaceScopedRestore RestoreAutoCheckout(
		[LoadSaveSettings] { LoadSaveSettings->ResetAutomaticallyCheckoutOnAssetModificationOverride(); });

	// Idempotent re-import: wipe any prior content for THIS order so reimported assets land on
	// clean names (Interchange re-creates source-named assets that the importer then renames).
	//
	// Guarded, because the folder is named by the TRUNCATED identity and the delete is a force-
	// delete. Two identities sharing eight characters name the same folder, and without the
	// provenance record this would silently destroy a different order's imported content. The
	// record is read from outside the content tree on purpose — reading an asset inside a folder
	// we are about to force-delete is the hazard described at the delete itself.
	if (UEditorAssetSubsystem* AssetSubsystem = GEditor->GetEditorSubsystem<UEditorAssetSubsystem>())
	{
		const bool bFolderExists = AssetSubsystem->DoesDirectoryExist(DestPackagePath);
		MantlePlaceImportProvenance::FRecord Prior;
		const bool bHasRecord = MantlePlaceImportProvenance::Read(Identity, Prior);
		const MantlePlaceImportProvenance::EVerdict Verdict =
			MantlePlaceImportProvenance::Classify(bFolderExists, bHasRecord, Prior, Identity);

		const FString Refusal =
			MantlePlaceImportProvenance::ExplainRefusal(Verdict, Prior, DestPackagePath);
		if (!Refusal.IsEmpty())
		{
			// Before the transaction opens and before anything is created, so a refusal changes
			// nothing at all -- including the auto-checkout override, which RestoreAutoCheckout
			// puts back on the way out of this return.
			Result.Message = Refusal;
			return Result;
		}

		if (Verdict == MantlePlaceImportProvenance::EVerdict::SameIdentity)
		{
			// Clear the editor selection first: DeleteDirectory force-deletes, and force-deleting a
			// selected/referenced asset drives UpdatePivotLocationForSelection over a now-stale typed-
			// element selection, tripping the "Element type ID 0 not registered" ensure. An empty
			// selection makes that path a no-op.
			GEditor->SelectNone(/*bNoteSelectionChange*/ false, /*bDeselectBSPSurfs*/ true, /*bWarnAboutManyActors*/ false);
			AssetSubsystem->DeleteDirectory(DestPackagePath);
		}
	}

	// glTF asset imports run BEFORE the transaction opens, and everything below this line is either
	// an actor operation or an asset creation that deletes nothing.
	//
	// A .glb brings embedded textures and materials with it, named after the source (Terrain_texture_0),
	// and naming those to the project standard means a rename. Renaming a freshly-imported asset
	// inside the import's own transaction takes the whole import off the undo stack -- ObjectTools
	// resets the entire buffer when the object it is deleting is referenced only by that buffer
	// (see MantlePlaceImportNaming::ImportNameFor). Measured 2026-08-30 on a Both-mode import:
	// Terrain.glb's texture rename purged the buffer and Ctrl+Z then moved nothing at all.
	//
	// The one thing that must still precede this is the idempotent wipe above: it force-deletes a
	// prior import's assets, and doing it AFTER would delete what we just imported.
	UStaticMesh* TerrainMesh = nullptr;
	FString TerrainMeshError, TerrainMeshDisk;
	if (bWantMesh && Manifest.bHasMesh)
	{
		if (ExtractEntry(Reader, Manifest.MeshPath, TempDir, TerrainMeshDisk, TerrainMeshError))
		{
			TerrainMesh = MantlePlaceMeshImporter::ImportMeshAsset(
				Manifest, TerrainMeshDisk, DestPackagePath, /*bEnableNanite*/ true, TerrainMeshError);
		}
	}

	UStaticMesh* BuildingsMesh = nullptr;
	FString BuildingsMeshError, BuildingsMeshDisk;
	if (Manifest.bHasBuildings)
	{
		if (ExtractEntry(Reader, Manifest.BuildingsPath, TempDir, BuildingsMeshDisk, BuildingsMeshError))
		{
			BuildingsMesh = MantlePlaceMeshImporter::ImportMeshAsset(
				Manifest, BuildingsMeshDisk, DestPackagePath, /*bEnableNanite*/ false, BuildingsMeshError);
		}
	}

	FScopedTransaction Transaction(LOCTEXT("ImportVaultPackage", "Import Mantle Place Vault Package"));

	TArray<AActor*> DrapeTargets;
	bool bAllRequestedSucceeded = true;

	// Idempotent re-import (actors): remove the actors a PRIOR import of this same ORDER spawned,
	// so re-importing replaces them instead of stacking duplicate (coincident) actors.
	//
	// Matched on the import TAG rather than the label. A label is a thing a user edits: renaming an
	// actor in the outliner used to break that user's own next re-import, silently, and then stack a
	// second landscape on top of the first. A tag is not surfaced for editing, carries the FULL
	// identity rather than its truncation, and survives the actor being dragged elsewhere.
	//
	// Its absence is also the legacy signal: an actor from 0.3.0 or earlier has an MP_* label and no
	// tag, so it is not matched here — and the content guard above has already refused the import
	// rather than leaving those actors orphaned beside new ones.
	{
		const FName ImportTagName(*MantlePlaceImportNaming::ImportTag(Identity));
		TArray<AActor*> StaleActors;
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			if (It->Tags.Contains(ImportTagName))
			{
				StaleActors.Add(*It);
			}
		}
		for (AActor* Stale : StaleActors)
		{
			World->EditorDestroyActor(Stale, /*bShouldModifyLevel*/ true);
		}
		if (StaleActors.Num() > 0)
		{
			Log.Add(FString::Printf(
				TEXT("Replaced %d actor(s) from a prior import of this bundle."), StaleActors.Num()));
		}
	}

	// Build the drape material up front: it depends only on the manifest + imagery texture, not the
	// geometry, so the Landscape can be created with it already assigned (a landscape only adopts its
	// material cleanly when it is set before ALandscape::Import builds the components). Non-fatal.
	UMaterialInstanceConstant* DrapeMic = nullptr;
	if (Manifest.bHasDrape)
	{
		FString Err, ImageryDisk;
		if (ExtractEntry(Reader, Manifest.DrapePath, TempDir, ImageryDisk, Err))
		{
			if (UTexture2D* Texture = MantlePlaceDrape::ImportTexture(ImageryDisk, DestPackagePath, Err))
			{
				DrapeMic = MantlePlaceDrape::CreateDrapeMaterial(Manifest, Texture, DestPackagePath, Err);
			}
		}
		if (DrapeMic == nullptr)
		{
			// Geometry still imports; it just won't get the auto-assigned imagery.
			Log.Add(Err);
		}
	}

	// --- Landscape ---
	if (bWantLandscape)
	{
		if (!Manifest.bHasHeightmap)
		{
			Log.Add(TEXT("Landscape requested but the bundle has no heightmap."));
			bAllRequestedSucceeded = false;
		}
		else
		{
			// Material-weight layers, if the bundle published them. Non-fatal: a landscape with the
			// drape and no weight layers is what every bundle produced before this, so a decode
			// failure degrades to that rather than failing the import.
			TArray<FMantlePlaceWeightPlane> WeightPlanes;
			FIntPoint WeightRasterSize(0, 0); // what the planes were resampled FROM, for the log
			if (const FMantlePlaceLandscapeLayer* Weights = Manifest.FindLandscapeLayer(TEXT("material_weights")))
			{
				FString WeightsErr;
				TArray<FMantlePlaceRgbaImage> Images;
				bool bDecoded = true;
				for (const FMantlePlaceUeReadyRaster& Raster : Weights->UeReady)
				{
					WeightRasterSize = FIntPoint(Raster.Width, Raster.Height);
					FString RasterDisk;
					FMantlePlaceRgbaImage Image;
					if (!ExtractEntry(Reader, Raster.Path, TempDir, RasterDisk, WeightsErr)
						|| !MantlePlaceLandscapeImporter::DecodeRgbaPng(RasterDisk, Raster.Path, Image, WeightsErr))
					{
						bDecoded = false;
						break;
					}
					Images.Add(MoveTemp(Image));
				}
				if (bDecoded)
				{
					bDecoded = FMantlePlaceLandscapeWeightsLogic::BuildWeightPlanes(
						*Weights, Images, Manifest.Resolution, Manifest.bRow0IsNorth, WeightPlanes, WeightsErr);
				}
				if (!bDecoded)
				{
					WeightPlanes.Reset();
					Log.Add(FString::Printf(TEXT("Landscape material layers skipped: %s"), *WeightsErr));
				}
			}

			FString Err, HeightmapDisk;
			if (ExtractEntry(Reader, Manifest.HeightmapPath, TempDir, HeightmapDisk, Err))
			{
				if (ALandscape* Landscape = MantlePlaceLandscapeImporter::Import(
						World, Manifest, HeightmapDisk, DrapeMic, WeightPlanes, DestPackagePath, Err))
				{
					ClaimImportedActor(Landscape, Identity, MantlePlaceImportNaming::ActorLabel(
						MantlePlaceImportNaming::EActorKind::Landscape, Identity));
					DrapeTargets.Add(Landscape);
					Result.CreatedActors.Add(Landscape->GetActorLabel());
					Log.Add(FString::Printf(TEXT("Landscape created (%dx%d)."), Manifest.Resolution, Manifest.Resolution));
					if (WeightPlanes.Num() > 0)
					{
						// The weight rasters are on the DEM's grid, not the Landscape's, so say what
						// was resampled onto what rather than letting the numbers look identical.
						Log.Add(FString::Printf(
							TEXT("Painted %d material weight layer(s), resampled from %dx%d onto the %dx%d grid."),
							WeightPlanes.Num(), WeightRasterSize.X, WeightRasterSize.Y,
							Manifest.Resolution, Manifest.Resolution));
					}
				}
				else
				{
					Log.Add(FString::Printf(TEXT("Landscape failed: %s"), *Err));
					bAllRequestedSucceeded = false;
				}
			}
			else
			{
				Log.Add(Err);
				bAllRequestedSucceeded = false;
			}
		}
	}

	// --- Coverage rasters ---
	// The `unreal.landscape_layers` sub-blocks other than material_weights: water_mask, worldcover,
	// hillshade, ndvi, slope, aspect, canopy_height. Each becomes a UTexture2D carrying its decoded
	// value_mapping and provenance, so a material or PCG graph can read real metres and degrees
	// rather than a 0-65535 ramp. Nothing in this plugin samples them — what they LOOK like is
	// product design, and the contract's obligation is that they are reachable with meaning.
	//
	// Independent of Mode: they describe the ground rather than being a terrain representation, so
	// like the buildings and road splines they import whenever the bundle ships them. Per-raster
	// warn-and-skip, deliberately unlike the fail-closed weights path — a missing analysis channel
	// costs a texture, not a wrong Landscape. (Their bytes are still fail-closed: a sha256 mismatch
	// aborted in the integrity pre-check above, before any actor.)
	{
		int32 RastersImported = 0;
		TArray<FString> SkippedLayers;
		for (const FMantlePlaceLandscapeLayer& Layer : Manifest.LandscapeLayers)
		{
			if (!FMantlePlaceCoverageRasterLogic::IsCoverageRaster(Layer.Name))
			{
				continue;
			}
			for (const FMantlePlaceUeReadyRaster& Raster : Layer.UeReady)
			{
				FString Err, RasterDisk;
				UTexture2D* Texture = nullptr;
				if (ExtractEntry(Reader, Raster.Path, TempDir, RasterDisk, Err))
				{
					Texture = MantlePlaceCoverageRasters::ImportRaster(
						Layer, Raster, RasterDisk, Manifest.JobId, DestPackagePath, Err);
				}
				if (Texture != nullptr)
				{
					++RastersImported;
				}
				else
				{
					SkippedLayers.Add(Layer.Name);
					Log.Add(FString::Printf(TEXT("Coverage raster \"%s\" skipped: %s"), *Layer.Name, *Err));
				}
			}
		}
		if (RastersImported > 0)
		{
			Log.Add(FString::Printf(
				TEXT("Imported %d coverage raster(s) as data textures with their value mappings attached."),
				RastersImported));
		}
		if (SkippedLayers.Num() > 0)
		{
			// Name the skipped set as a set, not only one line each: a reader scanning the log for
			// what they got needs the gap to be as visible as the success count.
			Log.Add(FString::Printf(TEXT("Coverage rasters not imported: %s."), *FString::Join(SkippedLayers, TEXT(", "))));
		}
	}

	// --- Mesh ---
	if (bWantMesh)
	{
		if (!Manifest.bHasMesh)
		{
			// v8 bundles ship Cesium quantized-mesh terrain (Terrain/) instead of a glb, so the
			// Landscape is the primary representation. In Both mode an absent mesh is informational,
			// not a failure; only an explicit Mesh-only request treats it as a failure.
			if (Mode == EMantlePlaceImportMode::Both)
			{
				Log.Add(TEXT("No static mesh in this bundle (Cesium terrain only) — imported the Landscape."));
			}
			else if (!Manifest.MeshAbsentReason.IsEmpty())
			{
				// The pipeline told us why it shipped no mesh — relay it so the failure points back
				// at the ETL (e.g. "mesh_not_produced") rather than reading as an importer fault.
				Log.Add(FString::Printf(
					TEXT("Mesh requested but this bundle has no static mesh (pipeline: %s). The ETL did "
						 "not generate a mesh for this AOI — re-export once the mesh stage is producing it."),
					*Manifest.MeshAbsentReason));
				bAllRequestedSucceeded = false;
			}
			else
			{
				Log.Add(TEXT("Mesh requested but the bundle has no static mesh."));
				bAllRequestedSucceeded = false;
			}
		}
		else
		{
			// The asset was imported above, before the transaction; this only spawns for it.
			FString Err = TerrainMeshError;
			AStaticMeshActor* MeshActor = TerrainMesh != nullptr
				? MantlePlaceMeshImporter::Import(World, Manifest, TerrainMesh, Err)
				: nullptr;
			if (MeshActor != nullptr)
			{
				DrapeTargets.Add(MeshActor);
				if (DrapeMic != nullptr && !MantlePlaceDrape::AssignMaterial(MeshActor, DrapeMic))
				{
					// The mesh imported and is in the level; only the imagery is missing. Said in
					// the import log rather than only in the output log, because "the terrain is
					// grey" is what the user actually sees and this is the sentence that explains
					// it. AssignMaterial has already logged which of its refusals this was.
					Log.Add(TEXT("WARNING: the imagery drape could not be assigned to the terrain "
								 "mesh — see the output log for which engine property is missing."));
					bAllRequestedSucceeded = false;
				}
				ClaimImportedActor(MeshActor, Identity, MantlePlaceImportNaming::ActorLabel(
					MantlePlaceImportNaming::EActorKind::Mesh, Identity));
				Result.CreatedActors.Add(MeshActor->GetActorLabel());
				Log.Add(TEXT("Mesh (Terrain.glb) created."));
			}
			else
			{
				Log.Add(FString::Printf(TEXT("Mesh failed: %s"), *Err));
				bAllRequestedSucceeded = false;
			}
		}
	}

	// --- Buildings ---
	// Extruded building massing (ALL / "Unreal" scope). It is content, not a terrain representation, so
	// it auto-imports whenever present regardless of Mode — alongside the Landscape and/or terrain mesh.
	// It shares the terrain's Local Projected Frame (GetMeshLocation) so it rests on the ground, and it
	// takes no imagery drape. A buildings failure here is non-fatal (mirrors the drape): the terrain is
	// the primary deliverable, so we log it rather than failing the whole import. (A buildings sha256
	// mismatch is still fail-closed — it aborts in the integrity pre-check above, before any actor.)
	if (Manifest.bHasBuildings)
	{
		// The asset was imported above, before the transaction; this only spawns for it.
		FString Err = BuildingsMeshError;
		AStaticMeshActor* BuildingsActor = BuildingsMesh != nullptr
			? MantlePlaceMeshImporter::ImportBuildings(World, Manifest, BuildingsMesh, Err)
			: nullptr;
		if (BuildingsActor != nullptr)
		{
			ClaimImportedActor(BuildingsActor, Identity, MantlePlaceImportNaming::ActorLabel(
				MantlePlaceImportNaming::EActorKind::Buildings, Identity));
			Result.CreatedActors.Add(BuildingsActor->GetActorLabel());
			Log.Add(TEXT("Buildings (Buildings.glb) created."));
		}
		else
		{
			Log.Add(FString::Printf(TEXT("Buildings import skipped: %s"), *Err));
		}
	}

	// --- Road splines ---
	// Z-draped road centerlines -> one spline actor per road (Wave-2 pipeline layers). Like the
	// buildings, this is content, not a terrain representation: it auto-imports whenever the bundle
	// ships the layer, regardless of Mode, and a failure is non-fatal. Width/class/name land as
	// actor tags so PCG or a road tool can consume them without re-reading the bundle.
	if (Manifest.bHasRoadSplines)
	{
		TArray<uint8> GeoJsonBytes;
		FString GeoJsonText, SplinesError;
		TArray<FMantlePlaceRoadSpline> Splines;
		bool bParsed = false;
		if (!Reader.TryReadFile(Manifest.RoadSplinesPath, GeoJsonBytes))
		{
			Log.Add(FString::Printf(TEXT("Road splines skipped: bundle is missing %s."), *Manifest.RoadSplinesPath));
		}
		else
		{
			FFileHelper::BufferToString(GeoJsonText, GeoJsonBytes.GetData(), GeoJsonBytes.Num());
			bParsed = FMantlePlaceRoadSplinesLogic::ParseGeoJson(
			    GeoJsonText, Manifest.OriginEastingM, Manifest.OriginNorthingM, Manifest.Epsg, Splines, SplinesError);
			if (!bParsed)
			{
				Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *SplinesError));
			}
		}
		if (bParsed)
		{
			int32 SplineIndex = 0;
			for (const FMantlePlaceRoadSpline& Spline : Splines)
			{
				AActor* SplineActor = World->SpawnActor<AActor>();
				if (SplineActor == nullptr)
				{
					continue;
				}
				USplineComponent* SplineComponent = NewObject<USplineComponent>(
				    SplineActor, USplineComponent::StaticClass(), TEXT("RoadSpline"), RF_Transactional);
				SplineActor->SetRootComponent(SplineComponent);
				SplineActor->AddInstanceComponent(SplineComponent);
				SplineComponent->RegisterComponent();

				SplineComponent->ClearSplinePoints(/*bUpdateSpline*/ false);
				for (const FVector& Point : Spline.PointsUeCm)
				{
					SplineComponent->AddSplinePoint(Point, ESplineCoordinateSpace::World, /*bUpdateSpline*/ false);
				}
				SplineComponent->UpdateSpline();

				ClaimImportedActor(SplineActor, Identity,
				    MantlePlaceImportNaming::RoadSplineLabel(Identity, SplineIndex++));
				SplineActor->Tags.Add(FName(*FString::Printf(TEXT("width_m=%.1f"), Spline.WidthMEstimated)));
				if (!Spline.RoadClass.IsEmpty())
				{
					SplineActor->Tags.Add(FName(*FString::Printf(TEXT("class=%s"), *Spline.RoadClass)));
				}
				if (!Spline.Name.IsEmpty())
				{
					SplineActor->Tags.Add(FName(*FString::Printf(TEXT("name=%s"), *Spline.Name)));
				}
				Result.CreatedActors.Add(SplineActor->GetActorLabel());
			}
			Log.Add(FString::Printf(TEXT("Road splines created (%d spline actor(s))."), SplineIndex));
		}
	}

	// --- Tree points ---
	// Resolved from the bundle's own unreal.foliage_points.path pointer (HPS-32/HPS-33) — never
	// layout.tree_points or landcover.tree_points, which are other blocks' pointers to the same
	// layer. Absence means the bundle simply doesn't ship the layer (base tier / treeless AOI), not
	// an error. Rows land in a UDataTable under the bundle's content folder: PCG-ready scatter
	// input, no actors spawned.
	if (Manifest.bHasFoliagePoints)
	{
		TArray<uint8> CsvBytes;
		if (!Reader.TryReadFile(Manifest.FoliagePointsPath, CsvBytes))
		{
			Log.Add(FString::Printf(TEXT("Tree points skipped: bundle is missing %s."), *Manifest.FoliagePointsPath));
		}
		else
		{
			FString CsvText, TreesError;
			FFileHelper::BufferToString(CsvText, CsvBytes.GetData(), CsvBytes.Num());
			TArray<FMantlePlaceTreePointRow> Rows;
			if (!FMantlePlaceTreePointsLogic::ParseCsv(
			        CsvText, Manifest.OriginEastingM, Manifest.OriginNorthingM, Rows, TreesError))
			{
				Log.Add(FString::Printf(TEXT("Tree points skipped: %s"), *TreesError));
			}
			else
			{
				const FString AssetName = MantlePlaceImportNaming::TreePointsTableName();
				const FString PackageName =
					MantlePlaceImportNaming::SubfolderPath(
						DestPackagePath, MantlePlaceImportNaming::ESubfolder::Landcover)
					/ AssetName;
				UPackage* Package = CreatePackage(*PackageName);
				// RF_Transactional is set AFTER RowStruct, not passed to NewObject. A transactional
				// object constructed while a transaction is open is serialized into the undo buffer
				// there and then, and UDataTable::Serialize logs an Error when it is written with no
				// RowStruct -- which is precisely its state on the line between these two. Latent
				// until the import's transaction started surviving to the end (see
				// MantlePlaceImportNaming::ImportNameFor); before that the buffer was purged out
				// from under it and the snapshot never happened.
				UDataTable* Table = NewObject<UDataTable>(Package, FName(*AssetName), RF_Public | RF_Standalone);
				Table->RowStruct = FMantlePlaceTreePointRow::StaticStruct();
				Table->SetFlags(RF_Transactional);
				for (int32 RowIndex = 0; RowIndex < Rows.Num(); ++RowIndex)
				{
					Table->AddRow(
						FName(*MantlePlaceImportNaming::TreePointsRowName(RowIndex)), Rows[RowIndex]);
				}
				FAssetRegistryModule::AssetCreated(Table);
				Table->MarkPackageDirty();
				Log.Add(FString::Printf(TEXT("Tree points imported (%d rows -> %s)."), Rows.Num(), *AssetName));
			}
		}
	}

	// --- Drape status + coverage sanity check ---
	// The Landscape adopted DrapeMic at creation (set before ALandscape::Import, above) and meshes
	// were assigned it right after their import, so there is nothing left to assign here — just
	// report the outcome and sanity-check coverage.
	if (DrapeTargets.Num() > 0)
	{
		if (!Manifest.bHasDrape)
		{
			Log.Add(TEXT("No imagery drape in this bundle (geometry only)."));
		}
		else if (DrapeMic != nullptr)
		{
			Log.Add(TEXT("Imagery draped onto its geographic footprint."));

			// Sanity-check imagery coverage against the terrain. The drape is placed at its true
			// geographic footprint; if the bundle's imagery spans only part of the AOI it will not
			// blanket the terrain (a web-ETL data issue, not an import fault). Surface it here so the
			// mismatch is visible rather than silently mis-scaled.
			if (Manifest.bHasHeightmap)
			{
				// Both spans in UE axis order (X = North, Y = East) — GetAoiSizeUeCm owns the swap
				// between the manifest's grid-axis naming and UE's, so this must not open-code it.
				const FVector2D AoiSize = Manifest.GetAoiSizeUeCm();
				const double SpanXcm = AoiSize.X;
				const double SpanYcm = AoiSize.Y;
				FVector2D DrapeMin, DrapeSize;
				Manifest.GetDrapeWorldRect(DrapeMin, DrapeSize);
				if (SpanXcm > 0.0 && SpanYcm > 0.0
					&& (DrapeSize.X < SpanXcm * 0.99 || DrapeSize.Y < SpanYcm * 0.99))
				{
					Log.Add(FString::Printf(
						TEXT("WARNING: imagery footprint (%.0f x %.0f m) covers only %.0f%% x %.0f%% of the "
							 "terrain (%.0f x %.0f m) — bundle imagery does not span the AOI (web ETL issue)."),
						DrapeSize.X / 100.0, DrapeSize.Y / 100.0,
						100.0 * DrapeSize.X / SpanXcm, 100.0 * DrapeSize.Y / SpanYcm,
						SpanXcm / 100.0, SpanYcm / 100.0));
				}
			}
		}
		// else: a drape was requested but the material failed to build — the error was logged above.
	}

	Result.bSuccess = bAllRequestedSucceeded && Result.CreatedActors.Num() > 0;
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

	if (!Manifest.bHasCesiumTerrain)
	{
		Info.Message = TEXT("This bundle ships no Cesium quantized-mesh terrain (layout.cesiumTerrain is "
			"absent). Re-export from a v8+ pipeline that produces Cesium terrain tiles.");
		return Info;
	}

	// Zip-entry prefix derived from the manifest so this keeps working as the bundle layout renames
	// the terrain folder across versions (v13+: CesiumTerrain/; legacy: Terrain/).
	const FString TerrainPrefix = MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(Manifest.CesiumTerrainPath);
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
	const FString TerrainRootOnDisk = FPaths::Combine(StageDir, Manifest.CesiumTerrainPath);

	MantlePlaceStreamStaging::FRecord Incoming;
	Incoming.Identity = Identity;
	Incoming.ManifestSha256 = ManifestSha256;
	Incoming.TerrainPrefix = TerrainPrefix;
	Incoming.CesiumTerrainPath = Manifest.CesiumTerrainPath;
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

		const TArray<FString> Prefixes = { TerrainPrefix, TEXT("Imagery/") };
		Incoming.EntryCount = ExtractSubtree(Reader, Prefixes, StageDir);
		if (Incoming.EntryCount == 0)
		{
			Info.Message = FString::Printf(TEXT("Bundle declares Cesium terrain but no %s entries were extracted."), *TerrainPrefix);
			return Info;
		}

		// Correct the bundle's over-declared `available` so Cesium only requests tiles that exist,
		// and log whether it needed correcting — see RewriteCesiumTerrainAvailability.
		RewriteCesiumTerrainAvailability(TerrainRootOnDisk, Manifest.JobId);

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
	Info.CesiumTerrainUrl = BaseUrl / Manifest.CesiumTerrainPath;
	if (Manifest.bHasDrape && !Manifest.DrapePath.IsEmpty())
	{
		Info.ImageryUrl = BaseUrl / Manifest.DrapePath;
	}
	Info.bHasBbox = Manifest.bHasBbox;
	Info.BboxWestDeg = Manifest.BboxWestDeg;
	Info.BboxSouthDeg = Manifest.BboxSouthDeg;
	Info.BboxEastDeg = Manifest.BboxEastDeg;
	Info.BboxNorthDeg = Manifest.BboxNorthDeg;
	Info.Message = FString::Printf(
		TEXT("Streaming bundle %s on %s (%d Cesium terrain tiles). Cesium3DTileset Url -> %s"),
		*MantlePlaceImportNaming::ShortIdentity(Manifest.JobId), *BaseUrl, Manifest.CesiumTerrainTileCount, *Info.CesiumTerrainUrl);
	return Info;
}

void UMantlePlaceImporterLibrary::StopBundleStream()
{
	if (GBundleStreamServer.IsValid())
	{
		GBundleStreamServer->Stop();
	}
}

#undef LOCTEXT_NAMESPACE
