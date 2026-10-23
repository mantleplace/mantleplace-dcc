// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceImporterLibrary.generated.h"

struct FMantlePlaceVaultManifest; // MantlePlaceImportManifest.h (out-param of ReadVaultManifest)
struct FMantlePlaceImportSelection; // MantlePlaceArtifactHandler.h (editor-internal)

/**
 * Entry point for the local-zip vault-package importer. The Editor Utility Widget calls
 * ImportVaultPackage; everything else (unzip, manifest parse, Landscape/Mesh creation,
 * imagery drape) happens in C++.
 */
UCLASS()
class MANTLEPLACEEDITOR_API UMantlePlaceImporterLibrary : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Import a downloaded Mantle Place vault bundle (.zip) into the current editor level:
	 * build a Landscape and/or static Mesh from the pre-baked assets and drape the aerial
	 * imagery onto its true geographic footprint. The whole import is one undo transaction.
	 *
	 * @param ZipPath  Absolute path to the bundle .zip on disk.
	 * @param Mode     Landscape, Mesh, or Both.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Import")
	static FMantlePlaceImportResult ImportVaultPackage(
		const FString& ZipPath,
		EMantlePlaceImportMode Mode = EMantlePlaceImportMode::Landscape);

	/**
	 * The same import, driven by an explicit selection of artifacts rather than the three-way Mode:
	 * the entry point a per-layer selection UI calls. ImportVaultPackage is this with
	 * FMantlePlaceImportSelection::FromMode(Mode). Not a UFUNCTION - the selection is a plain struct.
	 */
	static FMantlePlaceImportResult ImportVaultPackageWithSelection(
		const FString& ZipPath, const FMantlePlaceImportSelection& Selection);

	/**
	 * Read + parse a bundle .zip's Metadata/manifest.json WITHOUT importing anything. Lets the local
	 * import flow decide, before importing, whether the bundle already ships its Unreal formats
	 * (OutManifest.bValid) or must be materialized on demand first (OutManifest.OrderId is the join key
	 * back to the vault order). Not a UFUNCTION - FMantlePlaceVaultManifest is a plain (non-Blueprint) struct.
	 *
	 * @return  false + OutError only for a structural failure (missing file / unreadable zip / no
	 *          manifest.json). A bundle that parses but has no `unreal` block returns TRUE with
	 *          OutManifest.bValid == false and OutError holding the user-facing guidance.
	 */
	static bool ReadVaultManifest(const FString& ZipPath, FMantlePlaceVaultManifest& OutManifest, FString& OutError);

	/**
	 * Open a native file-open dialog filtered to *.zip and return the chosen bundle path.
	 * Editor-only; the Editor Utility Widget's Browse button calls this so the user never types a path.
	 *
	 * @param OutZipPath  Absolute path of the chosen file (left unchanged if the user cancels).
	 * @return            True if a file was picked, false if the dialog was cancelled or unavailable.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Import")
	static bool BrowseForVaultZip(FString& OutZipPath);

	/**
	 * Stream a downloaded bundle (.zip) into Cesium for Unreal from a local loopback server. Extracts the
	 * bundle's own Cesium-ready quantized-mesh terrain (CesiumTerrain/, or legacy Terrain/) + imagery and
	 * hosts them on 127.0.0.1 so an ACesium3DTileset can read them — alongside, not instead of, the
	 * native-asset ImportVaultPackage.
	 * This is download-to-own: nothing is streamed from the platform, only from the user's local copy.
	 * Returns the served URLs + AOI bbox; the editor Python wiring spawns the Cesium actors from them.
	 *
	 * @param ZipPath  Absolute path to the bundle .zip on disk.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Cesium")
	static FMantlePlaceStreamInfo StreamBundleIntoCesium(const FString& ZipPath);

	/** Stop the local bundle server started by StreamBundleIntoCesium (safe to call when not running). */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Cesium")
	static void StopBundleStream();

	/**
	 * Build the World Partition HLODs of the current editor level, after an import has been saved:
	 * the explicit post-import step, since the import itself never builds them. Runs the engine's own
	 * HLOD builder with no dialog and saves nothing.
	 *
	 * @return  The line the import log gets: the HLOD actor count and what the build cost, or why it
	 *          was refused (not World Partition, or not saved yet) and built nothing.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Import", meta = (ScriptName = "BuildHlods"))
	static FString BuildHLODs();

	/**
	 * Prepare for handover: make the current editor level safe to ship or share. Everything the import
	 * filed under ODbL (imported from `database` files, a missing role included) is removed, or swapped
	 * for the Produced Work of the same group already in the level where there is one, and the credit is
	 * added. One undo transaction; nothing is saved. The same command as Tools > Mantle Place >
	 * Prepare for handover and the console command `MantlePlace.PrepareForHandover`.
	 *
	 * @return  What it did: the counts, and any note the user should read.
	 */
	UFUNCTION(BlueprintCallable, Category = "Mantle Place|Import", meta = (ScriptName = "PrepareForHandover"))
	static FString PrepareForHandover();
};
