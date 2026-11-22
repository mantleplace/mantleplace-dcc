// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceStreamStaging.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceStreamStagingTest,
    "MantlePlace.Import.StreamStaging",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	MantlePlaceStreamStaging::FRecord MakeRecord()
	{
		MantlePlaceStreamStaging::FRecord Record;
		Record.Identity = TEXT("ord-11112222-3333");
		Record.ManifestSha256 =
			TEXT("deadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00d");
		Record.TerrainPrefix = TEXT("CesiumTerrain/");
		Record.CesiumTerrainPath = TEXT("CesiumTerrain/layer.json");
		Record.DrapePath = TEXT("Imagery/Drape.png");
		Record.EntryCount = 42;
		Record.SchemeVersion = MantlePlaceStreamStaging::CurrentSchemeVersion;
		return Record;
	}
}

bool FMantlePlaceStreamStagingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceStreamStaging;

	const FRecord Incoming = MakeRecord();

	{
		TestTrue(TEXT("an exact match with its content on disk is reused"),
			Classify(true, Incoming, Incoming, true)
				== EVerdict::Reuse);
	}

	{
		TestTrue(TEXT("no record stages"),
			Classify(false, FRecord(), Incoming, true) == EVerdict::Stage);

		TestTrue(TEXT("a record whose layer.json is gone stages"),
			Classify(true, Incoming, Incoming, false) == EVerdict::Stage);

		FRecord Empty = Incoming;
		Empty.EntryCount = 0;
		TestTrue(TEXT("an extraction that wrote nothing is not a staged bundle"),
			Classify(true, Empty, Incoming, true) == EVerdict::Stage);
	}

	{
		FRecord Rebuilt = Incoming;
		Rebuilt.ManifestSha256 = TEXT("0000000011111111222222223333333344444444555555556666666677777777");
		TestTrue(TEXT("the same order rebuilt stages again"),
			Classify(true, Rebuilt, Incoming, true) == EVerdict::Stage);

		FRecord NoDigest = Incoming;
		NoDigest.ManifestSha256 = FString();
		FRecord IncomingNoDigest = Incoming;
		IncomingNoDigest.ManifestSha256 = FString();
		TestTrue(TEXT("two records agreeing on an EMPTY digest still stage — nothing was compared"),
			Classify(true, NoDigest, IncomingNoDigest, true) == EVerdict::Stage);
	}

	{
		FRecord Other = Incoming;
		Other.Identity = TEXT("ord-99998888-7777");
		TestTrue(TEXT("a record for another order stages"),
			Classify(true, Other, Incoming, true) == EVerdict::Stage);

		FRecord Cased = Incoming;
		Cased.Identity = Incoming.Identity.ToUpper();
		TestTrue(TEXT("an identity differing only in case is not the same identity"),
			Classify(true, Cased, Incoming, true) == EVerdict::Stage);
	}

	{
		FRecord Legacy = Incoming;
		Legacy.TerrainPrefix = TEXT("Terrain/");
		TestTrue(TEXT("a moved terrain prefix stages"),
			Classify(true, Legacy, Incoming, true) == EVerdict::Stage);

		FRecord MovedPointer = Incoming;
		MovedPointer.CesiumTerrainPath = TEXT("CesiumTerrain/tileset.json");
		TestTrue(TEXT("a moved terrain pointer stages"),
			Classify(true, MovedPointer, Incoming, true) == EVerdict::Stage);
	}

	{
		FRecord Moved = Incoming;
		Moved.DrapePath = TEXT("Imagery/Other.png");
		TestTrue(TEXT("a moved drape pointer stages"),
			Classify(true, Moved, Incoming, true) == EVerdict::Stage);

		FRecord NoDrape = Incoming;
		NoDrape.DrapePath = FString();
		TestTrue(TEXT("a drape that appeared stages"),
			Classify(true, NoDrape, Incoming, true) == EVerdict::Stage);
		TestTrue(TEXT("a drape that went away stages"),
			Classify(true, Incoming, NoDrape, true) == EVerdict::Stage);

		FRecord SchemeOne = Incoming;
		SchemeOne.SchemeVersion = 1;
		TestTrue(TEXT("a directory staged by the folder-name layout stages again"),
			Classify(true, SchemeOne, Incoming, true) == EVerdict::Stage);
	}

	{
		TestTrue(TEXT("a terrain tile under the pointer's directory is extracted"),
			SelectsEntry(Incoming, TEXT("CesiumTerrain/14/5615/11520.terrain")));
		TestTrue(TEXT("the pointed-at layer.json is extracted"),
			SelectsEntry(Incoming, TEXT("CesiumTerrain/layer.json")));
		TestTrue(TEXT("the served drape is extracted"),
			SelectsEntry(Incoming, TEXT("Imagery/Drape.png")));

		TestFalse(TEXT("a COG beside the drape is not extracted"),
			SelectsEntry(Incoming, TEXT("Imagery/Imagery.tif")));
		TestFalse(TEXT("a PMTiles archive beside the drape is not extracted"),
			SelectsEntry(Incoming, TEXT("Imagery/Imagery.pmtiles")));
		TestFalse(TEXT("the manifest is not extracted"),
			SelectsEntry(Incoming, TEXT("Metadata/manifest.json")));
		TestFalse(TEXT("a directory entry is not extracted"),
			SelectsEntry(Incoming, TEXT("CesiumTerrain/14/")));
		TestFalse(TEXT("a name that only begins with the drape's is not the drape"),
			SelectsEntry(Incoming, TEXT("Imagery/Drape.png.aux.xml")));

		TestTrue(TEXT("a terrain tile is extracted whatever the case of the folder"),
			SelectsEntry(Incoming, TEXT("cesiumterrain/14/5615/11520.terrain")));
		TestTrue(TEXT("the drape is extracted whatever its case"),
			SelectsEntry(Incoming, TEXT("IMAGERY/DRAPE.PNG")));
		TestFalse(TEXT("ignoring case does not widen the drape match to a longer name"),
			SelectsEntry(Incoming, TEXT("imagery/drape.png.aux.xml")));

		FRecord NoDrape = Incoming;
		NoDrape.DrapePath = FString();
		TestFalse(TEXT("with no served drape, no imagery is extracted"),
			SelectsEntry(NoDrape, TEXT("Imagery/Drape.png")));
		TestTrue(TEXT("and the terrain still is"),
			SelectsEntry(NoDrape, TEXT("CesiumTerrain/0/0/0.terrain")));

		FRecord NoPrefix = NoDrape;
		NoPrefix.TerrainPrefix = FString();
		TestFalse(TEXT("an empty terrain prefix selects nothing"),
			SelectsEntry(NoPrefix, TEXT("CesiumTerrain/0/0/0.terrain")));
	}

	{
		FRecord Newer = Incoming;
		Newer.SchemeVersion = CurrentSchemeVersion + 1;
		TestTrue(TEXT("a newer staging layout stages"),
			Classify(true, Newer, Incoming, true) == EVerdict::Stage);

		FRecord Older = Incoming;
		Older.SchemeVersion = 0;
		TestTrue(TEXT("a record with no layout stages"),
			Classify(true, Older, Incoming, true) == EVerdict::Stage);
	}

	{
		FRecord Parsed;
		TestTrue(TEXT("a written record reads back"), FromJson(ToJson(Incoming), Parsed));
		TestEqual(TEXT("identity survives"), Parsed.Identity, Incoming.Identity);
		TestEqual(TEXT("digest survives"), Parsed.ManifestSha256, Incoming.ManifestSha256);
		TestEqual(TEXT("terrain prefix survives"), Parsed.TerrainPrefix, Incoming.TerrainPrefix);
		TestEqual(TEXT("terrain pointer survives"), Parsed.CesiumTerrainPath, Incoming.CesiumTerrainPath);
		TestEqual(TEXT("drape pointer survives"), Parsed.DrapePath, Incoming.DrapePath);
		TestEqual(TEXT("entry count survives"), Parsed.EntryCount, Incoming.EntryCount);
		TestEqual(TEXT("scheme survives"), Parsed.SchemeVersion, Incoming.SchemeVersion);

		TestTrue(TEXT("a round-tripped record still reuses"),
			Classify(true, Parsed, Incoming, true) == EVerdict::Reuse);
	}

	{
		FRecord Parsed;
		TestFalse(TEXT("nonsense is not a record"), FromJson(TEXT("not json at all"), Parsed));
		TestFalse(TEXT("an empty string is not a record"), FromJson(FString(), Parsed));
		TestFalse(TEXT("a record naming no bundle is not a record"),
			FromJson(TEXT("{\"entry_count\":42}"), Parsed));
		TestFalse(TEXT("an empty identity is not a record"),
			FromJson(TEXT("{\"identity\":\"\"}"), Parsed));
	}

	{
		const FString Identity = TEXT("ord-11112222-3333");
		const FString Dir = StagingDir(Identity);
		const FString Record = RecordPath(Identity);

		TestTrue(TEXT("the staging directory is named by the FULL identity, not the truncation"),
			Dir.EndsWith(TEXT("/") + Identity));
		TestFalse(TEXT("the record is not inside the served directory"),
			Record.StartsWith(Dir + TEXT("/")));
		TestTrue(TEXT("the record sits beside it"), Record.EndsWith(Identity + TEXT(".json")));

		TestNotEqual(TEXT("a short-form collision does not collide here"),
			StagingDir(TEXT("ord-1111aaaa")), StagingDir(TEXT("ord-1111bbbb")));
	}

	return true;
}

#endif
