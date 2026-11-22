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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceBundleLayoutTest,
    "MantlePlace.Import.BundleLayout",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
struct FSyntheticBundle
{
	const TCHAR* Name = TEXT("");
	const TCHAR* Version = TEXT("");
	bool b2 = false;
	bool bShuffleCase = false;
	bool bUpperEntryKeys = false;

	FString Terrain;
	FString Parcels;
	FString Heightmap;
	FString Drape;
	FString Mesh;
	FString WaterMask;

	FString Extra;
	bool bParcelsInArchive = false;
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

TArray<uint8> PayloadOf(const FString& Path)
{
	return BytesOf(FString::Printf(TEXT("synthetic payload of %s"), *Path.ToLower()));
}

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
		"{'job_id':'synthetic-@NAME@','version':'@VERSION@',@EXTRA@"
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
	Text.ReplaceInline(TEXT("@EXTRA@"), *B.Extra);
	Text.ReplaceInline(TEXT("'"), TEXT("\""));
	return Text;
}

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
		FZipArchiveWriter Writer(Out);
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
		if (B.bParcelsInArchive)
		{
			Writer.AddFile(Spell(B.Parcels), PayloadOf(B.Parcels), Now);
		}
	}
	return true;
}

void CheckBundle(FAutomationTestBase& T, const FSyntheticBundle& B)
{
	const FString Label = FString::Printf(TEXT("[%s] "), B.Name);
	const FString ZipPath = FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), FGuid::NewGuid().ToString() + TEXT(".zip"));
	const FString ManifestText = ManifestOf(B);
	ON_SCOPE_EXIT { IFileManager::Get().Delete(*ZipPath, false, true); };

	if (!T.TestTrue(Label + TEXT("the synthetic zip is written"), WriteZip(B, ZipPath, ManifestText, false)))
	{
		return;
	}

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

	if (B.b2)
	{
		T.TestTrue(Label + TEXT("the heightmap's role is its stated one, whatever case the key is in"),
			MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, B.Heightmap) == EMantlePlaceOdblRole::ProducedWork);
		T.TestTrue(Label + TEXT("and whatever case the pointer is asked in"),
			MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, B.Heightmap.ToLower()) == EMantlePlaceOdblRole::ProducedWork);
	}
	T.TestTrue(Label + TEXT("a file the manifest states nothing about reads as the stricter duty"),
		MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Site/Unlisted.glb")) == EMantlePlaceOdblRole::Database);

	{
		IFileHandle* Handle = FPlatformFileManager::Get().GetPlatformFile().OpenRead(*ZipPath);
		if (!T.TestNotNull(Label + TEXT("the zip opens for the pre-check"), Handle))
		{
			return;
		}
		FZipArchiveReader Reader(Handle);
		MantlePlaceImportTiming::FTimeline Timeline;
		FString IntegrityError;
		T.TestTrue(
			Label + FString::Printf(TEXT("every payload verifies against its pointer (%s)"), *IntegrityError),
			MantlePlaceImportOrchestration::VerifyAll(M, Reader, Timeline, IntegrityError));
	}

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

struct FSelectionOutcome
{
	bool bOpened = false;
	bool bPassed = false;
	FString Error;
	TArray<FString> Notes;
};

FSelectionOutcome VerifyCut(FAutomationTestBase& T, const FString& Label, const FString& ViewJson, bool bParcelsInArchive)
{
	FSelectionOutcome Outcome;
	FSyntheticBundle B = Make2(false, false);
	B.bParcelsInArchive = bParcelsInArchive;
	B.Extra = FString::Printf(
		TEXT("'files':{'%s':{'part':'records.parcels_gpkg@MAX','size_bytes':1,'sha256':'%s'}},%s"),
		*B.Parcels, *MantlePlaceSha256::HexDigest(PayloadOf(B.Parcels)), *ViewJson);

	const FString ZipPath = FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), FGuid::NewGuid().ToString() + TEXT(".zip"));
	ON_SCOPE_EXIT { IFileManager::Get().Delete(*ZipPath, false, true); };
	if (!T.TestTrue(Label + TEXT("the synthetic zip is written"), WriteZip(B, ZipPath, ManifestOf(B), false)))
	{
		return Outcome;
	}
	FMantlePlaceVaultManifest M;
	FString ReadError;
	UMantlePlaceImporterLibrary::ReadVaultManifest(ZipPath, M, ReadError);
	if (!T.TestTrue(Label + FString::Printf(TEXT("the manifest is accepted (%s)"), *ReadError), M.bValid))
	{
		return Outcome;
	}
	IFileHandle* Handle = FPlatformFileManager::Get().GetPlatformFile().OpenRead(*ZipPath);
	if (!T.TestNotNull(Label + TEXT("the zip opens for the pre-check"), Handle))
	{
		return Outcome;
	}
	Outcome.bOpened = true;
	FZipArchiveReader Reader(Handle);
	MantlePlaceImportTiming::FTimeline Timeline;
	Outcome.bPassed = MantlePlaceImportOrchestration::VerifyAll(M, Reader, Timeline, Outcome.Error, &Outcome.Notes);
	return Outcome;
}

void CheckSelections(FAutomationTestBase& T)
{
	const FString Parcels = TEXT("Records/Parcels.gpkg");
	const auto Names = [&Parcels](const FSelectionOutcome& O)
	{
		return O.Notes.ContainsByPredicate([&Parcels](const FString& Note) { return Note.Contains(Parcels); });
	};

	{
		const FSelectionOutcome O = VerifyCut(T, TEXT("[whole archive] "), FString(), true);
		if (O.bOpened)
		{
			T.TestTrue(TEXT("[whole archive] every payload verifies, the parcels file included"), O.bPassed);
			T.TestEqual(TEXT("[whole archive] and nothing is reported left out"), O.Notes.Num(), 0);
		}
	}

	{
		const FSelectionOutcome O = VerifyCut(T, TEXT("[selection] "),
			TEXT("'view':{'kind':'selection','partial':false,'parts':['imagery.drape_png@MAX'],'missing':[]},"),
			false);
		if (O.bOpened)
		{
			T.TestTrue(TEXT("[selection] a payload the selection left out does not fail the pre-check"), O.bPassed);
			T.TestTrue(TEXT("[selection] and the log names it"), Names(O));
			T.TestEqual(TEXT("[selection] and names nothing else"), O.Notes.Num(), 1);
		}
	}

	{
		const FSelectionOutcome O = VerifyCut(T, TEXT("[held part missing] "),
			TEXT("'view':{'kind':'selection','partial':false,'parts':['records.parcels_gpkg@MAX'],'missing':[]},"),
			false);
		if (O.bOpened)
		{
			T.TestFalse(TEXT("[held part missing] a missing entry in a part the archive holds is still refused"), O.bPassed);
			T.TestTrue(TEXT("[held part missing] and the refusal names the entry"), O.Error.Contains(Parcels));
		}
	}

	{
		const FSelectionOutcome O = VerifyCut(T, TEXT("[everything] "),
			TEXT("'view':{'kind':'everything','partial':false,'parts':['imagery.drape_png@MAX'],'missing':[]},"),
			false);
		if (O.bOpened)
		{
			T.TestFalse(TEXT("[everything] a sealed archive missing a declared entry is refused"), O.bPassed);
			T.TestTrue(TEXT("[everything] and the refusal names the entry"), O.Error.Contains(Parcels));
		}
	}
	{
		const FSelectionOutcome O = VerifyCut(T, TEXT("[no view] "), FString(), false);
		if (O.bOpened)
		{
			T.TestFalse(TEXT("[no view] an archive that states no view and lacks a declared entry is refused"), O.bPassed);
			T.TestTrue(TEXT("[no view] and the refusal names the entry"), O.Error.Contains(Parcels));
		}
	}
}
}

bool FMantlePlaceBundleLayoutTest::RunTest(const FString& Parameters)
{
	CheckSelections(*this);

	CheckBundle(*this, Make1x());
	CheckBundle(*this, Make2(false, false));

	CheckBundle(*this, Make2(true, true));

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

#endif
