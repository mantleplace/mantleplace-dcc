// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImporterLibrary.h"

#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceCoverageRasters.h"
#include "MantlePlaceDrape.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLandscapeImporter.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceLocalTileServer.h"
#include "MantlePlaceMeshImporter.h"
#include "MantlePlaceRoadSplinesLogic.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceTreePointsLogic.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Engine/DataTable.h"
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
	 * Rewrite a quantized-mesh layer.json `available` array to list exactly the tiles present on disk.
	 *
	 * ETL bundles ship a layer.json whose low-zoom `available` rectangles declare the whole-world pyramid
	 * (e.g. level 1 claims x[0..3] y[0..1] = 8 tiles) while only the single AOI-ancestor tile per level is
	 * actually written. Cesium for Unreal trusts `available`, requests the declared-but-absent siblings,
	 * gets 404s, and aborts with "Errors loading quantized mesh terrain" — nothing renders. The web
	 * app compensates by rewriting `available`; this plugin's self-contained local server must do the
	 * same. We emit one inclusive 1x1 rectangle per present tile, grouped by zoom. The present tiles form a
	 * connected descent chain to the AOI (every tile's parent exists), so refinement still works. This is
	 * a workaround for an upstream packaging defect (the bundle's own layer.json is wrong) — report it
	 * upstream rather than treating this as the final home of the fix. Best-effort: on any failure the original
	 * file is left untouched and streaming proceeds (Cesium will 404 the siblings as before).
	 */
	void RewriteCesiumTerrainAvailability(const FString& LayerJsonPath)
	{
		FString JsonText;
		if (!FFileHelper::LoadFileToString(JsonText, *LayerJsonPath))
		{
			return;
		}
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> JsonReader = TJsonReaderFactory<>::Create(JsonText);
		if (!FJsonSerializer::Deserialize(JsonReader, Root) || !Root.IsValid())
		{
			return;
		}

		// Collect present tiles: <TerrainDir>/{z}/{x}/{y}.terrain. Derive z/x/y from the path components
		// (robust to separator style) rather than string-relativizing.
		const FString TerrainDir = FPaths::GetPath(LayerJsonPath);
		TArray<FString> TileFiles;
		IFileManager::Get().FindFilesRecursive(TileFiles, *TerrainDir, TEXT("*.terrain"), /*Files*/ true, /*Dirs*/ false);
		if (TileFiles.Num() == 0)
		{
			return;
		}

		TMap<int32, TArray<TTuple<int32, int32>>> TilesByZoom;
		int32 MaxZoom = -1;
		for (const FString& TilePath : TileFiles)
		{
			const FString YStr = FPaths::GetBaseFilename(TilePath);          // {y}
			const FString XDir = FPaths::GetPath(TilePath);                  // .../{z}/{x}
			const FString XStr = FPaths::GetCleanFilename(XDir);             // {x}
			const FString ZStr = FPaths::GetCleanFilename(FPaths::GetPath(XDir)); // {z}
			if (!ZStr.IsNumeric() || !XStr.IsNumeric() || !YStr.IsNumeric())
			{
				continue;
			}
			const int32 Z = FCString::Atoi(*ZStr);
			const int32 X = FCString::Atoi(*XStr);
			const int32 Y = FCString::Atoi(*YStr);
			TilesByZoom.FindOrAdd(Z).Add(MakeTuple(X, Y));
			MaxZoom = FMath::Max(MaxZoom, Z);
		}
		if (MaxZoom < 0)
		{
			return;
		}

		TArray<TSharedPtr<FJsonValue>> Available;
		Available.Reserve(MaxZoom + 1);
		for (int32 Z = 0; Z <= MaxZoom; ++Z)
		{
			TArray<TSharedPtr<FJsonValue>> LevelRects;
			if (const TArray<TTuple<int32, int32>>* Level = TilesByZoom.Find(Z))
			{
				for (const TTuple<int32, int32>& XY : *Level)
				{
					const TSharedPtr<FJsonObject> Rect = MakeShared<FJsonObject>();
					Rect->SetNumberField(TEXT("startX"), XY.Get<0>());
					Rect->SetNumberField(TEXT("startY"), XY.Get<1>());
					Rect->SetNumberField(TEXT("endX"), XY.Get<0>());
					Rect->SetNumberField(TEXT("endY"), XY.Get<1>());
					LevelRects.Add(MakeShared<FJsonValueObject>(Rect));
				}
			}
			Available.Add(MakeShared<FJsonValueArray>(LevelRects));
		}
		Root->SetArrayField(TEXT("available"), Available);

		FString OutText;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutText);
		if (FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
		{
			FFileHelper::SaveStringToFile(OutText, *LayerJsonPath);
		}
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
		*Manifest.Version, *Manifest.JobId.Left(8)));

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
		*Manifest.JobId.Left(8));

	UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		Result.Message = TEXT("No editor world is open to import into.");
		return Result;
	}

	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ImportTmp") / Manifest.JobId;
	PlatformFile.CreateDirectoryTree(*TempDir);
	const FString DestPackagePath = FString::Printf(TEXT("/Game/MantlePlace/%s"), *Manifest.JobId.Left(8));

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

	// These are freshly generated assets, not yet in source control. Suppress the editor's
	// auto-checkout-on-modify for the duration of the import so renames/edits don't spam the
	// connected SCC provider with checkouts of files that aren't under source control. Runtime-only
	// override; reset before returning.
	UEditorLoadingSavingSettings* LoadSaveSettings = GetMutableDefault<UEditorLoadingSavingSettings>();
	LoadSaveSettings->SetAutomaticallyCheckoutOnAssetModificationOverride(false);

	// Idempotent re-import: wipe any prior content for this bundle so reimported assets land on
	// clean names (Interchange re-creates source-named assets that the importer then renames).
	if (UEditorAssetSubsystem* AssetSubsystem = GEditor->GetEditorSubsystem<UEditorAssetSubsystem>())
	{
		if (AssetSubsystem->DoesDirectoryExist(DestPackagePath))
		{
			// Clear the editor selection first: DeleteDirectory force-deletes, and force-deleting a
			// selected/referenced asset drives UpdatePivotLocationForSelection over a now-stale typed-
			// element selection, tripping the "Element type ID 0 not registered" ensure. An empty
			// selection makes that path a no-op.
			GEditor->SelectNone(/*bNoteSelectionChange*/ false, /*bDeselectBSPSurfs*/ true, /*bWarnAboutManyActors*/ false);
			AssetSubsystem->DeleteDirectory(DestPackagePath);
		}
	}

	FScopedTransaction Transaction(LOCTEXT("ImportVaultPackage", "Import Mantle Place Vault Package"));

	TArray<AActor*> DrapeTargets;
	bool bAllRequestedSucceeded = true;

	// Idempotent re-import (actors): remove any Landscape/Mesh actors a PRIOR import of this same
	// bundle spawned, so re-importing replaces them instead of stacking duplicate (coincident)
	// actors. Both importers label their actors "MP_<Type>_<jobId8>".
	{
		const FString JobIdShort = Manifest.JobId.Left(8);
		const FString LandscapeLabel = FString::Printf(TEXT("MP_Landscape_%s"), *JobIdShort);
		const FString MeshLabel = FString::Printf(TEXT("MP_Mesh_%s"), *JobIdShort);
		const FString BuildingsLabel = FString::Printf(TEXT("MP_Buildings_%s"), *JobIdShort);
		const FString RoadSplinePrefix = FString::Printf(TEXT("MP_RoadSpline_%s_"), *JobIdShort);
		TArray<AActor*> StaleActors;
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			const FString Label = It->GetActorLabel();
			if (Label == LandscapeLabel || Label == MeshLabel || Label == BuildingsLabel || Label.StartsWith(RoadSplinePrefix))
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
			FString Err, GlbDisk;
			if (ExtractEntry(Reader, Manifest.MeshPath, TempDir, GlbDisk, Err))
			{
				if (AStaticMeshActor* MeshActor =
						MantlePlaceMeshImporter::Import(World, Manifest, GlbDisk, DestPackagePath, /*bEnableNanite*/ true, Err))
				{
					DrapeTargets.Add(MeshActor);
					if (DrapeMic != nullptr)
					{
						MantlePlaceDrape::AssignMaterial(MeshActor, DrapeMic);
					}
					Result.CreatedActors.Add(MeshActor->GetActorLabel());
					Log.Add(TEXT("Mesh (Terrain.glb) created."));
				}
				else
				{
					Log.Add(FString::Printf(TEXT("Mesh failed: %s"), *Err));
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

	// --- Buildings ---
	// Extruded building massing (ALL / "Unreal" scope). It is content, not a terrain representation, so
	// it auto-imports whenever present regardless of Mode — alongside the Landscape and/or terrain mesh.
	// It shares the terrain's Local Projected Frame (GetMeshLocation) so it rests on the ground, and it
	// takes no imagery drape. A buildings failure here is non-fatal (mirrors the drape): the terrain is
	// the primary deliverable, so we log it rather than failing the whole import. (A buildings sha256
	// mismatch is still fail-closed — it aborts in the integrity pre-check above, before any actor.)
	if (Manifest.bHasBuildings)
	{
		FString Err, BuildingsDisk;
		AStaticMeshActor* BuildingsActor = nullptr;
		if (ExtractEntry(Reader, Manifest.BuildingsPath, TempDir, BuildingsDisk, Err))
		{
			BuildingsActor = MantlePlaceMeshImporter::ImportBuildings(World, Manifest, BuildingsDisk, DestPackagePath, Err);
		}
		if (BuildingsActor != nullptr)
		{
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
			const FString JobIdShort = Manifest.JobId.Left(8);
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

				SplineActor->SetActorLabel(FString::Printf(TEXT("MP_RoadSpline_%s_%03d"), *JobIdShort, SplineIndex++));
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
				const FString AssetName = FString::Printf(TEXT("DT_TreePoints_%s"), *Manifest.JobId.Left(8));
				const FString PackageName = DestPackagePath / TEXT("Landcover") / AssetName;
				UPackage* Package = CreatePackage(*PackageName);
				UDataTable* Table = NewObject<UDataTable>(Package, FName(*AssetName), RF_Public | RF_Standalone | RF_Transactional);
				Table->RowStruct = FMantlePlaceTreePointRow::StaticStruct();
				for (int32 RowIndex = 0; RowIndex < Rows.Num(); ++RowIndex)
				{
					Table->AddRow(FName(*FString::Printf(TEXT("Tree_%d"), RowIndex)), Rows[RowIndex]);
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

	LoadSaveSettings->ResetAutomaticallyCheckoutOnAssetModificationOverride();

	Result.bSuccess = bAllRequestedSucceeded && Result.CreatedActors.Num() > 0;
	Result.Message = FString::Join(Log, TEXT("\n"));
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

	// Lay the bundle's Cesium-ready artifacts on disk (the per-bundle temp dir the importer already uses)
	// for the local tile server to host.
	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ImportTmp") / Manifest.JobId;
	PlatformFile.CreateDirectoryTree(*TempDir);
	const TArray<FString> Prefixes = { TerrainPrefix, TEXT("Imagery/") };
	if (ExtractSubtree(Reader, Prefixes, TempDir) == 0)
	{
		Info.Message = FString::Printf(TEXT("Bundle declares Cesium terrain but no %s entries were extracted."), *TerrainPrefix);
		return Info;
	}

	// Correct the bundle's over-declared `available` so Cesium only requests tiles that exist (see
	// RewriteCesiumTerrainAvailability). Without this the tileset 404s its way to a load error.
	RewriteCesiumTerrainAvailability(FPaths::Combine(TempDir, Manifest.CesiumTerrainPath));

	// Start (or restart) the loopback server rooted at the extracted bundle dir. Try a small port range
	// so a busy default port doesn't block streaming.
	if (!GBundleStreamServer.IsValid())
	{
		GBundleStreamServer = MakeUnique<FMantlePlaceLocalTileServer>();
	}
	FString BaseUrl, ServerError;
	for (uint32 Port = 8088; Port <= 8095 && BaseUrl.IsEmpty(); ++Port)
	{
		BaseUrl = GBundleStreamServer->Start(TempDir, Port, ServerError);
	}
	if (BaseUrl.IsEmpty())
	{
		Info.Message = FString::Printf(TEXT("Failed to start local tile server: %s"), *ServerError);
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
		*Manifest.JobId.Left(8), *BaseUrl, Manifest.CesiumTerrainTileCount, *Info.CesiumTerrainUrl);
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
