// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceOdblLogic.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceVaultTypes.h"

#include "FileUtilities/ZipArchiveReader.h"
#include "FileUtilities/ZipArchiveWriter.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/Guid.h"
#include "Misc/ScopeExit.h"
#include "Misc/Paths.h"

// The importer reads a 1.x bundle and a 2.0 bundle, finds every file through the path the manifest
// points at, and matches that path to the archive entry regardless of case (MPB 2.0, spec/format.md
// 10.3 rule 6). Nothing here needs an editor world: a synthetic bundle is written as a real zip, opened
// the way the importer opens one (UMantlePlaceImporterLibrary::ReadVaultManifest), parsed, and walked by
// the integrity pre-check, which reads every declared payload out of the zip by its pointer.
//
// The bundles are synthetic: a few bytes per payload and a manifest with only the members the importer
// reads, not a bundle the platform would produce. What the layouts change is the pointer values (the
// folder names and the basenames) and where a PNG's pixel meaning is stated (a 1.x host block, a 2.0
// `file_entries` entry); both are exercised.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceBundleLayoutTest,
    "MantlePlace.Import.BundleLayout",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
/** One synthetic bundle: the manifest version, the pointer values and how the archive spells them. */
struct FSyntheticBundle
{
	const TCHAR* Name = TEXT("");
	const TCHAR* Version = TEXT("");
	bool b2 = false;            // the 2.0 shape: no host-block mapping, the meaning in `file_entries`
	bool bShuffleCase = false;  // the archive holds every entry, the manifest included, in UPPER case
	bool bUpperEntryKeys = false; // `file_entries` keys spelled in UPPER case against the pointers

	FString Terrain;     // layout.cesium_terrain
	FString Parcels;     // layout.parcels_gpkg
	FString Heightmap;   // hosts.unreal.heightmap.path
	FString Drape;       // hosts.unreal.imagery_drape.source
	FString Mesh;        // hosts.unreal.mesh_alternative.path
	FString WaterMask;   // hosts.unreal.landscape_layers.water_mask.ue_ready[0].path
};

FSyntheticBundle Make1x()
{
	FSyntheticBundle B;
	B.Name = TEXT("1.15.0");
	B.Version = TEXT("1.15.0");
	B.Terrain = TEXT("CesiumTerrain/layer.json");
	B.Parcels = TEXT("Parcels/Parcels.gpkg");
	B.Heightmap = TEXT("Elevation/Heightmap.png");
	B.Drape = TEXT("Imagery/Drape.png");
	B.Mesh = TEXT("Mesh/Terrain.glb");
	B.WaterMask = TEXT("Landcover/WaterMask_UE.png");
	return B;
}

FSyntheticBundle Make2(bool bShuffleCase, bool bUpperEntryKeys)
{
	FSyntheticBundle B;
	B.Name = bShuffleCase ? TEXT("2.0.0, archive in another case") : TEXT("2.0.0");
	B.Version = TEXT("2.0.0");
	B.b2 = true;
	B.bShuffleCase = bShuffleCase;
	B.bUpperEntryKeys = bUpperEntryKeys;
	B.Terrain = TEXT("Elevation/Terrain/layer.json");
	B.Parcels = TEXT("Records/Parcels.gpkg");
	B.Heightmap = TEXT("Elevation/Heightmap.png");
	B.Drape = TEXT("Imagery/Imagery.png");
	B.Mesh = TEXT("Site/Terrain-Engine.glb");
	B.WaterMask = TEXT("LandCover/WaterMask-Engine.png");
	return B;
}

TArray<uint8> BytesOf(const FString& Text)
{
	const FTCHARToUTF8 Utf8(*Text);
	return TArray<uint8>(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
}

/** The payload of one pointer: distinct per path, so a read of the wrong entry is a hash mismatch. */
TArray<uint8> PayloadOf(const FString& Path)
{
	return BytesOf(FString::Printf(TEXT("synthetic payload of %s"), *Path.ToLower()));
}

/** A manifest built from one template, `'` standing for a double quote so the template stays readable. */
FString ManifestOf(const FSyntheticBundle& B)
{
	const FString HeightmapMapping = B.b2 ? FString() : FString(TEXT("'uint16_mapping':{'z_min_m':-10.0,'z_max_m':502.0},"));
	const FString WaterMeaning = B.b2
		? FString()
		: FString(TEXT(",'encoding':'png-8bit-mask','value_mapping':{'true_value':255,'false_value':0}"));

	const auto Key = [&B](const FString& Path) { return B.bUpperEntryKeys ? Path.ToUpper() : Path; };
	FString Entries;
	if (B.b2)
	{
		Entries = FString::Printf(
			TEXT("'%s':{'frame':{'placed':true},'odbl_role':'produced-work','license':'CC0-1.0','sources':[],")
			TEXT("'value_mapping':{'encoding':'png-16bit-grayscale','min':-10.0,'max':502.0,'value_units':'m'}},")
			TEXT("'%s':{'frame':{'placed':true},'odbl_role':'produced-work','license':'CC0-1.0','sources':[],")
			TEXT("'value_mapping':{'encoding':'png-8bit-mask','true_value':255,'false_value':0,'value_units':'none'}}"),
			*Key(B.Heightmap), *Key(B.WaterMask));
	}

	FString Text = TEXT(
		"{'job_id':'synthetic-@NAME@','version':'@VERSION@',"
		"'layout':{'cesium_terrain':'@TERRAIN@','parcels_gpkg':'@PARCELS@'},"
		"'parcels':{},"
		"'file_entries':{@ENTRIES@},"
		"'hosts':{'unreal':{"
		"'georeference':{'crs_projected':'EPSG:32618','planet_shape':'Flat','origin':{"
		"'ground_orthometric_h_m':20.0,'utm':{'easting_m':585000.0,'northing_m':4511000.0,'epsg':32618}}},"
		"'heightmap':{'path':'@HEIGHTMAP@','resolution':1009,'row0_is_north':true,'sha256':'@HEIGHTMAP_SHA@',"
		"@HEIGHTMAP_MAPPING@"
		"'landscape_transform':{'z_scale_percent':100.0,'scale_x_percent':100.0,'scale_y_percent':100.0,"
		"'location_z_offset_cm':0.0,'section_size_quads':63,'sections_per_component':2,"
		"'component_count_x':8,'component_count_y':8}},"
		"'imagery_drape':{'source':'@DRAPE@','sha256':'@DRAPE_SHA@','extent':[584000.0,4510000.0,586000.0,4512000.0]},"
		"'mesh_alternative':{'path':'@MESH@','up_axis':'y','sha256':'@MESH_SHA@'},"
		"'landscape_layers':{'water_mask':{'path':'@WATER_SOURCE@','ue_ready':["
		"{'path':'@WATER@','sha256':'@WATER_SHA@','width':4,'height':4@WATER_MEANING@}]}}"
		"}}}");
	Text.ReplaceInline(TEXT("@NAME@"), B.b2 ? TEXT("two") : TEXT("one"));
	Text.ReplaceInline(TEXT("@VERSION@"), B.Version);
	Text.ReplaceInline(TEXT("@TERRAIN@"), *B.Terrain);
	Text.ReplaceInline(TEXT("@PARCELS@"), *B.Parcels);
	Text.ReplaceInline(TEXT("@ENTRIES@"), *Entries);
	Text.ReplaceInline(TEXT("@HEIGHTMAP@"), *B.Heightmap);
	Text.ReplaceInline(TEXT("@HEIGHTMAP_SHA@"), *MantlePlaceSha256::HexDigest(PayloadOf(B.Heightmap)));
	Text.ReplaceInline(TEXT("@HEIGHTMAP_MAPPING@"), *HeightmapMapping);
	Text.ReplaceInline(TEXT("@DRAPE@"), *B.Drape);
	Text.ReplaceInline(TEXT("@DRAPE_SHA@"), *MantlePlaceSha256::HexDigest(PayloadOf(B.Drape)));
	Text.ReplaceInline(TEXT("@MESH@"), *B.Mesh);
	Text.ReplaceInline(TEXT("@MESH_SHA@"), *MantlePlaceSha256::HexDigest(PayloadOf(B.Mesh)));
	Text.ReplaceInline(TEXT("@WATER_SOURCE@"), TEXT("LandCover/WaterMask.tif"));
	Text.ReplaceInline(TEXT("@WATER@"), *B.WaterMask);
	Text.ReplaceInline(TEXT("@WATER_SHA@"), *MantlePlaceSha256::HexDigest(PayloadOf(B.WaterMask)));
	Text.ReplaceInline(TEXT("@WATER_MEANING@"), *WaterMeaning);
	Text.ReplaceInline(TEXT("'"), TEXT("\""));
	return Text;
}

/** Write the bundle as a real zip. Every entry, the manifest's included, in UPPER case when asked. */
bool WriteZip(const FSyntheticBundle& B, const FString& ZipPath, const FString& ManifestText, bool bCorruptHeightmap)
{
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	Platform.CreateDirectoryTree(*FPaths::GetPath(ZipPath));
	IFileHandle* Out = Platform.OpenWrite(*ZipPath);
	if (Out == nullptr)
	{
		return false;
	}
	const auto Spell = [&B](const FString& Name) { return B.bShuffleCase ? Name.ToUpper() : Name; };
	{
		FZipArchiveWriter Writer(Out); // takes ownership of the handle; the destructor writes the directory
		const FDateTime Now = FDateTime::UtcNow();
		Writer.AddFile(Spell(TEXT("README.md")), BytesOf(TEXT("synthetic bundle")), Now);
		Writer.AddFile(Spell(TEXT("Metadata/manifest.json")), BytesOf(ManifestText), Now);
		for (const FString& Path : { B.Heightmap, B.Drape, B.Mesh, B.WaterMask })
		{
			TArray<uint8> Bytes = PayloadOf(Path);
			if (bCorruptHeightmap && Path == B.Heightmap)
			{
				Bytes = BytesOf(TEXT("not what the manifest hashed"));
			}
			Writer.AddFile(Spell(Path), Bytes, Now);
		}
	}
	return true;
}

/** The pointer path to the entry the archive holds, as a function of the bundle's spelling. */
void CheckBundle(FAutomationTestBase& T, const FSyntheticBundle& B)
{
	const FString Label = FString::Printf(TEXT("[%s] "), B.Name);
	const FString ZipPath = FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), FGuid::NewGuid().ToString() + TEXT(".zip"));
	const FString ManifestText = ManifestOf(B);
	ON_SCOPE_EXIT { IFileManager::Get().Delete(*ZipPath, /*RequireExists*/ false, /*EvenReadOnly*/ true); };

	if (!T.TestTrue(Label + TEXT("the synthetic zip is written"), WriteZip(B, ZipPath, ManifestText, false)))
	{
		return;
	}

	// --- Opened the way the importer opens a bundle -------------------------------------------------
	FMantlePlaceVaultManifest M;
	FString Error;
	const bool bRead = UMantlePlaceImporterLibrary::ReadVaultManifest(ZipPath, M, Error);
	T.TestTrue(Label + TEXT("the zip opens and holds a manifest"), bRead);
	T.TestTrue(Label + FString::Printf(TEXT("the manifest is accepted (%s)"), *Error), M.bValid);
	T.TestEqual(Label + TEXT("the manifest version is kept verbatim"), M.Version, FString(B.Version));
	if (!M.bValid)
	{
		return;
	}

	// --- Every file is the one its pointer says, whatever the folder is called ------------------------
	T.TestEqual(Label + TEXT("heightmap pointer"), M.Heightmap.Path, B.Heightmap);
	T.TestEqual(Label + TEXT("drape pointer"), M.Drape.Path, B.Drape);
	T.TestEqual(Label + TEXT("terrain mesh pointer"), M.Mesh.Path, B.Mesh);
	T.TestEqual(Label + TEXT("Cesium terrain pointer"), M.Cesium.TerrainPath, B.Terrain);
	T.TestEqual(Label + TEXT("Cesium terrain prefix follows the pointer's directory"),
		MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(M.Cesium.TerrainPath),
		FPaths::GetPath(B.Terrain) + TEXT("/"));

	if (T.TestEqual(Label + TEXT("one public-record family"), M.PublicRecords.Layers.Num(), 1)
		&& T.TestEqual(Label + TEXT("its file"), M.PublicRecords.Layers[0].Files.Num(), 1))
	{
		T.TestEqual(Label + TEXT("the parcels file is found through the layout pointer"),
			M.PublicRecords.Layers[0].Files[0].Path, B.Parcels);
	}

	// --- What the pixels mean is read from where the layout states it ---------------------------------
	T.TestEqual(Label + TEXT("heightmap range, low"), M.Heightmap.ZMinM, -10.0);
	T.TestEqual(Label + TEXT("heightmap range, high"), M.Heightmap.ZMaxM, 502.0);
	const FMantlePlaceLandscapeLayer* Water = M.FindLandscapeLayer(TEXT("water_mask"));
	if (T.TestNotNull(Label + TEXT("the water mask layer is read"), Water)
		&& T.TestEqual(Label + TEXT("its UE-ready raster"), Water->UeReady.Num(), 1))
	{
		const FMantlePlaceUeReadyRaster& Raster = Water->UeReady[0];
		T.TestEqual(Label + TEXT("raster pointer"), Raster.Path, B.WaterMask);
		T.TestEqual(Label + TEXT("raster encoding"), Raster.Encoding, FString(TEXT("png-8bit-mask")));
		T.TestEqual(Label + TEXT("mask true value"), Raster.ValueMapping.TrueValue, 255.0);
		T.TestEqual(Label + TEXT("mask false value"), Raster.ValueMapping.FalseValue, 0.0);
	}

	// --- A file's terms are found regardless of the case of its key ------------------------------------
	if (B.b2)
	{
		T.TestTrue(Label + TEXT("the heightmap's role is its stated one, whatever case the key is in"),
			MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, B.Heightmap) == EMantlePlaceOdblRole::ProducedWork);
		T.TestTrue(Label + TEXT("and whatever case the pointer is asked in"),
			MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, B.Heightmap.ToLower()) == EMantlePlaceOdblRole::ProducedWork);
	}
	T.TestTrue(Label + TEXT("a file the manifest states nothing about reads as the stricter duty"),
		MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Site/Unlisted.glb")) == EMantlePlaceOdblRole::Database);

	// --- The pre-check reads every declared payload out of the zip by its pointer -----------------------
	{
		IFileHandle* Handle = FPlatformFileManager::Get().GetPlatformFile().OpenRead(*ZipPath);
		if (!T.TestNotNull(Label + TEXT("the zip opens for the pre-check"), Handle))
		{
			return;
		}
		FZipArchiveReader Reader(Handle); // takes ownership of the handle
		MantlePlaceImportTiming::FTimeline Timeline;
		FString IntegrityError;
		T.TestTrue(
			Label + FString::Printf(TEXT("every payload verifies against its pointer (%s)"), *IntegrityError),
			MantlePlaceImportOrchestration::VerifyAll(M, Reader, Timeline, IntegrityError));
	}

	// --- And a payload that is not the bytes the manifest hashed is still refused ------------------------
	{
		const FString BadZip = ZipPath + TEXT(".bad.zip");
		ON_SCOPE_EXIT { IFileManager::Get().Delete(*BadZip, false, true); };
		if (T.TestTrue(Label + TEXT("the corrupted zip is written"), WriteZip(B, BadZip, ManifestText, true)))
		{
			IFileHandle* Handle = FPlatformFileManager::Get().GetPlatformFile().OpenRead(*BadZip);
			if (T.TestNotNull(Label + TEXT("the corrupted zip opens"), Handle))
			{
				FZipArchiveReader Reader(Handle);
				MantlePlaceImportTiming::FTimeline Timeline;
				FString IntegrityError;
				T.TestFalse(Label + TEXT("a corrupted heightmap fails the pre-check"),
					MantlePlaceImportOrchestration::VerifyAll(M, Reader, Timeline, IntegrityError));
				T.TestTrue(Label + TEXT("and the refusal names the entry"),
					IntegrityError.Contains(B.Heightmap));
			}
		}
	}
}
} // namespace

bool FMantlePlaceBundleLayoutTest::RunTest(const FString& Parameters)
{
	// --- A 1.x bundle and a 2.0 bundle, each read through its own pointers ------------------------------
	CheckBundle(*this, Make1x());
	CheckBundle(*this, Make2(/*bShuffleCase*/ false, /*bUpperEntryKeys*/ false));

	// --- A 2.0 bundle whose archive and whose `file_entries` keys are in another case than its pointers -
	// The manifest says `Elevation/Heightmap.png` and the archive holds `ELEVATION/HEIGHTMAP.PNG`: the path
	// is the same path (format.md 10.3 rule 6), so the bundle reads, verifies and states the same terms.
	CheckBundle(*this, Make2(/*bShuffleCase*/ true, /*bUpperEntryKeys*/ true));

	// --- The engine's zip reader matches an entry regardless of case -----------------------------------
	// Checked against the engine source (UE 5.8, ZipArchiveReader.cpp: entries live in a TMap<FString,...>
	// whose hash and key comparison both ignore case) and here, against the engine as built: this is what
	// the importer's by-pointer reads rely on, and what Python's `zipfile` does not do.
	{
		const FString ZipPath = FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), FGuid::NewGuid().ToString() + TEXT(".zip"));
		ON_SCOPE_EXIT { IFileManager::Get().Delete(*ZipPath, false, true); };
		IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
		Platform.CreateDirectoryTree(*FPaths::GetPath(ZipPath));
		if (IFileHandle* Out = Platform.OpenWrite(*ZipPath))
		{
			FZipArchiveWriter Writer(Out);
			Writer.AddFile(TEXT("Metadata/Manifest.JSON"), BytesOf(TEXT("{}")), FDateTime::UtcNow());
		}
		IFileHandle* Handle = Platform.OpenRead(*ZipPath);
		if (TestNotNull(TEXT("the engine zip test file opens"), Handle))
		{
			FZipArchiveReader Reader(Handle);
			TArray<uint8> Bytes;
			TestTrue(TEXT("the engine's zip reader finds an entry in another case"),
				Reader.TryReadFile(TEXT("metadata/manifest.json"), Bytes));
			TestTrue(TEXT("and finds it by its own spelling"),
				Reader.TryReadFile(TEXT("Metadata/Manifest.JSON"), Bytes));
			TestFalse(TEXT("and does not find an entry that is not there"),
				Reader.TryReadFile(TEXT("Metadata/manifest.json.bak"), Bytes));
		}
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
