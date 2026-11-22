// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceBundleCache.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceStreamStaging.h"
#include "MantlePlaceTerrainTilesLogic.h"
#include "MantlePlaceVaultTypes.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "FileUtilities/ZipArchiveWriter.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonWriter.h"

namespace
{
	bool WriteZip(const FString& Path, const TArray<TPair<FString, FString>>& Entries)
	{
		IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
		Platform.CreateDirectoryTree(*FPaths::GetPath(Path));
		IFileHandle* Out = Platform.OpenWrite(*Path);
		if (Out == nullptr)
		{
			return false;
		}
		FZipArchiveWriter Writer(Out);
		const FDateTime Now = FDateTime::UtcNow();
		for (const TPair<FString, FString>& Entry : Entries)
		{
			const FTCHARToUTF8 Utf8(*Entry.Value);
			Writer.AddFile(Entry.Key, TArray<uint8>(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length()), Now);
		}
		return true;
	}

	bool CorpusManifestText(FString& OutText)
	{
		TSharedPtr<FJsonObject> Fixture;
		FString Unused;
		const TSharedPtr<FJsonObject>* Files = nullptr;
		const TSharedPtr<FJsonObject>* Manifest = nullptr;
		if (!MantlePlaceConformanceCorpus::Detail::LoadJsonObject(
				FPaths::Combine(MantlePlaceConformanceCorpus::FindCorpusRoot(), TEXT("bundle/v3-catalog-file-missing.json")), Fixture, Unused)
			|| !Fixture->TryGetObjectField(TEXT("files"), Files) || !(*Files)->TryGetObjectField(TEXT("Manifest.json"), Manifest))
		{
			return false;
		}
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutText);
		return FJsonSerializer::Serialize(Manifest->ToSharedRef(), Writer);
	}

	TArray<TPair<FString, FString>> TilesEntries(const FString& Folder)
	{
		return {
			{ Folder + TEXT("layer.json"), TEXT("{\"tilejson\":\"2.1.0\",\"format\":\"quantized-mesh-1.0\",\"available\":[[{\"startX\":0,\"startY\":0,\"endX\":0,\"endY\":0}]]}") },
			{ Folder + TEXT("0/0/0.terrain"), TEXT("tile") },
			{ Folder + TEXT("1/0/0.terrain"), TEXT("tile") },
		};
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceTerrainTilesLogicTest,
	"MantlePlace.Import.TerrainTiles",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTerrainTilesLogicTest::RunTest(const FString& Parameters)
{
	using L = FMantlePlaceTerrainTilesLogic;

	TestEqual(TEXT("beside the bundle"), L::TilesZipBeside(TEXT("D:/p/Saved/MantlePlace/VaultCache/order-1/bundle.zip")),
		FString(TEXT("D:/p/Saved/MantlePlace/VaultCache/order-1/TerrainTiles.zip")));
	TestEqual(TEXT("beside a zip of another name"), L::TilesZipBeside(TEXT("D:/Downloads/site.zip")),
		FString(TEXT("D:/Downloads/TerrainTiles.zip")));

	const FString Cache = TEXT("D:/p/Saved/MantlePlace/VaultCache");
	TestTrue(TEXT("a vault-cache bundle"), L::IsInVaultCache(TEXT("D:/p/Saved/MantlePlace/VaultCache/order-1/bundle.zip"), Cache));
	TestFalse(TEXT("a zip on disk"), L::IsInVaultCache(TEXT("D:/Downloads/site.zip"), Cache));
	TestFalse(TEXT("a sibling folder that shares the cache's name as a prefix"),
		L::IsInVaultCache(TEXT("D:/p/Saved/MantlePlace/VaultCacheOld/order-1/bundle.zip"), Cache));
	TestFalse(TEXT("a path that climbs out of the cache"),
		L::IsInVaultCache(TEXT("D:/p/Saved/MantlePlace/VaultCache/../Elsewhere/bundle.zip"), Cache));

	{
		const L::FTileset Found = L::FindTileset({ TEXT("Elevation/Terrain/layer.json"), TEXT("Elevation/Terrain/0/0/0.terrain"),
			TEXT("Elevation/Terrain/1/0/0.terrain"), TEXT("Readme.md") });
		TestTrue(TEXT("a tileset"), Found.bFound);
		TestEqual(TEXT("its layer.json"), Found.LayerJsonPath, FString(TEXT("Elevation/Terrain/layer.json")));
		TestEqual(TEXT("its folder"), Found.Prefix, FString(TEXT("Elevation/Terrain/")));
		TestEqual(TEXT("its tiles"), Found.TileCount, 2);
	}
	{
		const L::FTileset Found = L::FindTileset({ TEXT("A/B/C/layer.json"), TEXT("A/layer.json"), TEXT("A/1/0/0.terrain") });
		TestEqual(TEXT("the shallowest layer.json wins"), Found.Prefix, FString(TEXT("A/")));
	}
	{
		const L::FTileset Found = L::FindTileset({ TEXT("Terrain/LAYER.JSON"), TEXT("Terrain/0/0/0.TERRAIN") });
		TestTrue(TEXT("names match regardless of case"), Found.bFound && Found.TileCount == 1);
	}
	TestFalse(TEXT("a layer.json at the root would select every entry: not a tileset"),
		L::FindTileset({ TEXT("layer.json"), TEXT("0/0/0.terrain") }).bFound);
	TestFalse(TEXT("no layer.json: no tileset"), L::FindTileset({ TEXT("Terrain/0/0/0.terrain") }).bFound);
	TestFalse(TEXT("a folder entry is not a layer.json"), L::FindTileset({ TEXT("Terrain/layer.json/") }).bFound);

	TestTrue(TEXT("a 2.x bundle serves its own tiles, as before"),
		L::DecideSource(false, false) == L::ESource::Bundled && L::DecideSource(false, true) == L::ESource::Bundled);
	TestTrue(TEXT("a 3.x bundle with tiles beside it serves those"), L::DecideSource(true, true) == L::ESource::ExtraBeside);
	TestTrue(TEXT("a 3.x bundle with none beside it has none"), L::DecideSource(true, false) == L::ESource::ExtraMissing);

	{
		FMantlePlaceVaultItem Listed;
		FMantlePlaceVaultExtra Tiles;
		Tiles.Id = MantlePlaceTerrainTilesExtraId();
		Listed.Extras.Add(Tiles);
		FMantlePlaceVaultItem Unlisted;
		FMantlePlaceVaultExtra Brief;
		Brief.Id = TEXT("SiteBrief");
		Unlisted.Extras.Add(Brief);

		TestTrue(TEXT("tiles on disk: a second ask fetches nothing, whatever the listing says"),
			L::PlanFetch(true, &Listed) == L::EFetchPlan::Reuse && L::PlanFetch(true, nullptr) == L::EFetchPlan::Reuse);
		TestTrue(TEXT("listed and not on disk: fetch"), L::PlanFetch(false, &Listed) == L::EFetchPlan::Fetch);
		FMantlePlaceVaultItem Locked;
		FMantlePlaceVaultExtra LockedTiles;
		LockedTiles.Id = MantlePlaceTerrainTilesExtraId();
		LockedTiles.bLocked = true;
		Locked.Extras.Add(LockedTiles);
		TestTrue(TEXT("listed but locked on a free bundle: nothing is fetched, the platform would refuse it"),
			L::PlanFetch(false, &Locked) == L::EFetchPlan::Locked);
		TestTrue(TEXT("tiles already on disk are used even when the listing now locks them"),
			L::PlanFetch(true, &Locked) == L::EFetchPlan::Reuse);
		TestTrue(TEXT("the platform lists other Extras but no terrain tiles: disabled"),
			L::PlanFetch(false, &Unlisted) == L::EFetchPlan::NoTilesListed);
		TestTrue(TEXT("the listing holds no such order: disabled"), L::PlanFetch(false, nullptr) == L::EFetchPlan::NoTilesListed);
	}

	TestTrue(TEXT("a stated length is held to"), L::SizeMatches(31457, 31457) && !L::SizeMatches(31457, 31456));
	TestTrue(TEXT("a length the row did not state is not a mismatch"), L::SizeMatches(0, 99));

	TestTrue(TEXT("none listed names the cause"), L::NoTilesListedReason().Contains(TEXT("lists no terrain tiles")));
	TestEqual(TEXT("locked says the locked row's words"), L::LockedReason(), FString(MantlePlaceLockedExtrasWords()));
	TestTrue(TEXT("none beside names the file and the way on"),
		L::NoTilesBesideReason().Contains(L::TilesFileName()) && L::NoTilesBesideReason().Contains(TEXT("vault")));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceStreamTerrainTilesTest,
	"MantlePlace.Import.StreamTerrainTiles",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceStreamTerrainTilesTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}
	FString ManifestText;
	if (!TestTrue(TEXT("the corpus holds a 3.x manifest"), CorpusManifestText(ManifestText)))
	{
		return false;
	}

	IFileManager& Files = IFileManager::Get();
	const FString CacheRoot = FPaths::Combine(FPaths::ProjectSavedDir(), GetDefault<UMantlePlaceBundleCache>()->CacheSubDir);
	const FString Scratch = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), TEXT("StreamTerrainTiles-") + FGuid::NewGuid().ToString());
	const FString InCache = FPaths::Combine(CacheRoot, TEXT("test-") + FGuid::NewGuid().ToString());
	FString Identity;
	ON_SCOPE_EXIT
	{
		Files.DeleteDirectory(*Scratch, false, true);
		Files.DeleteDirectory(*InCache, false, true);
		if (!Identity.IsEmpty())
		{
			Files.DeleteDirectory(*MantlePlaceStreamStaging::StagingDir(Identity), false, true);
			Files.Delete(*MantlePlaceStreamStaging::RecordPath(Identity));
		}
	};

	const TArray<TPair<FString, FString>> Bundle = { { TEXT("Manifest.json"), ManifestText } };

	{
		const FString Zip = FPaths::Combine(Scratch, TEXT("site.zip"));
		if (!TestTrue(TEXT("the bundle zip is written"), WriteZip(Zip, Bundle)))
		{
			return false;
		}
		const FMantlePlaceStreamInfo Info = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(Zip);
		TestFalse(TEXT("a 3.x zip on disk with no tiles beside it does not stream"), Info.bSuccess);
		TestFalse(TEXT("and there is nothing to fetch for it"), Info.bTerrainTilesFetchable);
		TestEqual(TEXT("and says why"), Info.Message, FMantlePlaceTerrainTilesLogic::NoTilesBesideReason());
	}

	const FString CachedZip = FPaths::Combine(InCache, TEXT("bundle.zip"));
	{
		if (!TestTrue(TEXT("the cached bundle zip is written"), WriteZip(CachedZip, Bundle)))
		{
			return false;
		}
		const FMantlePlaceStreamInfo Info = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(CachedZip);
		TestFalse(TEXT("tiles not fetched: no stream"), Info.bSuccess);
		TestTrue(TEXT("a cached bundle's tiles can be fetched"), Info.bTerrainTilesFetchable);
		TestEqual(TEXT("and the message says to"), Info.Message, FMantlePlaceTerrainTilesLogic::NotFetchedYetMessage());
	}

	if (!TestTrue(TEXT("the tiles are written beside the bundle"),
			WriteZip(FMantlePlaceTerrainTilesLogic::TilesZipBeside(CachedZip), TilesEntries(TEXT("Elevation/Terrain/")))))
	{
		return false;
	}
	FMantlePlaceVaultManifest Read;
	FString ReadError;
	if (!TestTrue(TEXT("the 3.x bundle reads"), UMantlePlaceImporterLibrary::ReadVaultManifest(CachedZip, Read, ReadError)))
	{
		AddError(ReadError);
		return false;
	}
	Identity = MantlePlaceImportNaming::ResolveIdentity(Read.OrderId, FString());
	const FString StageDir = MantlePlaceStreamStaging::StagingDir(Identity);

	const FMantlePlaceStreamInfo First = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(CachedZip);
	ON_SCOPE_EXIT { UMantlePlaceImporterLibrary::StopBundleStream(); };
	if (!TestTrue(TEXT("with the tiles beside it a 3.x bundle streams"), First.bSuccess))
	{
		AddError(First.Message);
		return false;
	}
	TestTrue(TEXT("the terrain URL is the Extra's layer.json"), First.CesiumTerrainUrl.EndsWith(TEXT("Elevation/Terrain/layer.json")));
	TestTrue(TEXT("the tiles were staged"), Files.FileExists(*FPaths::Combine(StageDir, TEXT("Elevation/Terrain/1/0/0.terrain"))));
	TestTrue(TEXT("the site origin comes from the manifest, in degrees, verbatim"),
		First.bHasOrigin && FMath::IsNearlyEqual(First.OriginLongitudeDeg, -73.975) && FMath::IsNearlyEqual(First.OriginLatitudeDeg, 40.755));
	TestTrue(TEXT("the AOI rectangle comes from the manifest"), First.bHasBbox && FMath::IsNearlyEqual(First.BboxWestDeg, -74.02));
	TestTrue(TEXT("no imagery is served: the Extra holds none"), First.ImageryUrl.IsEmpty());

	const FString Marker = FPaths::Combine(StageDir, TEXT("reuse-marker.txt"));
	FFileHelper::SaveStringToFile(TEXT("kept"), *Marker);
	const FMantlePlaceStreamInfo Second = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(CachedZip);
	TestTrue(TEXT("a second ask streams"), Second.bSuccess);
	TestTrue(TEXT("and reused the staged tiles"), Files.FileExists(*Marker));

	if (WriteZip(FMantlePlaceTerrainTilesLogic::TilesZipBeside(CachedZip), TilesEntries(TEXT("Tiles/"))))
	{
		const FMantlePlaceStreamInfo Third = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(CachedZip);
		TestTrue(TEXT("changed tiles stream"), Third.bSuccess && Third.CesiumTerrainUrl.EndsWith(TEXT("Tiles/layer.json")));
		TestFalse(TEXT("and re-stage the directory"), Files.FileExists(*Marker));
	}

	{
		TArray<TPair<FString, FString>> Hostile = TilesEntries(TEXT("Elevation/Terrain/"));
		Hostile.Add({ TEXT("Elevation/Terrain/../../../escaped.txt"), TEXT("no") });
		WriteZip(FMantlePlaceTerrainTilesLogic::TilesZipBeside(CachedZip), Hostile);
		const FMantlePlaceStreamInfo Fourth = UMantlePlaceImporterLibrary::StreamBundleIntoCesium(CachedZip);
		TestTrue(TEXT("a hostile Extra still streams its tiles"), Fourth.bSuccess);
		TestFalse(TEXT("but nothing is written outside the staging directory"),
			Files.FileExists(*FPaths::Combine(FPaths::GetPath(StageDir), TEXT("escaped.txt"))));
	}
	return true;
}

#endif
