// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceStreamStaging.h"

// Whether a bundle already laid out on disk can be served as it stands, or has to be laid out again.
//
// Getting this wrong in the permissive direction serves the wrong terrain: the availability rewrite
// declares every .terrain file beneath the staging directory, so a stale tile is not merely unused,
// it is advertised. Getting it wrong in the strict direction costs an extraction. Every assertion
// below is written from that asymmetry — anything that does not match exactly re-stages.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceStreamStagingTest,
    "MantlePlace.Import.StreamStaging",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	/** A record for a staged bundle, as the streaming path writes one. */
	MantlePlaceStreamStaging::FRecord MakeRecord()
	{
		MantlePlaceStreamStaging::FRecord Record;
		Record.Identity = TEXT("ord-11112222-3333");
		Record.ManifestSha256 =
			TEXT("deadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00d");
		Record.TerrainPrefix = TEXT("CesiumTerrain/");
		Record.CesiumTerrainPath = TEXT("CesiumTerrain/layer.json");
		Record.EntryCount = 42;
		Record.SchemeVersion = MantlePlaceStreamStaging::CurrentSchemeVersion;
		return Record;
	}
}

bool FMantlePlaceStreamStagingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceStreamStaging;

	const FRecord Incoming = MakeRecord();

	// --- The one case that reuses ---------------------------------------------------------------
	{
		TestTrue(TEXT("an exact match with its content on disk is reused"),
			Classify(/*bRecordFound*/ true, Incoming, Incoming, /*bTerrainRootPresent*/ true)
				== EVerdict::Reuse);
	}

	// --- No record, or no content --------------------------------------------------------------
	{
		TestTrue(TEXT("no record stages"),
			Classify(false, FRecord(), Incoming, true) == EVerdict::Stage);

		// A user who cleaned their Saved directory leaves the record behind. A record is not content.
		TestTrue(TEXT("a record whose layer.json is gone stages"),
			Classify(true, Incoming, Incoming, /*bTerrainRootPresent*/ false) == EVerdict::Stage);

		FRecord Empty = Incoming;
		Empty.EntryCount = 0;
		TestTrue(TEXT("an extraction that wrote nothing is not a staged bundle"),
			Classify(true, Empty, Incoming, true) == EVerdict::Stage);
	}

	// --- A rebuilt order: the case identity alone would get wrong --------------------------------
	// The identity is the ORDER and is stable across rebuilds by design (ADR 0002), so a
	// re-materialized order arrives with the same identity and different tiles. Without the digest
	// this would serve the previous build's terrain, and the user would see a stale AOI with no
	// error anywhere.
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

	// --- A different bundle in the same slot ------------------------------------------------------
	{
		FRecord Other = Incoming;
		Other.Identity = TEXT("ord-99998888-7777");
		TestTrue(TEXT("a record for another order stages"),
			Classify(true, Other, Incoming, true) == EVerdict::Stage);

		// Case-sensitive: an order id and a hex digest are both case-significant as delivered.
		FRecord Cased = Incoming;
		Cased.Identity = Incoming.Identity.ToUpper();
		TestTrue(TEXT("an identity differing only in case is not the same identity"),
			Classify(true, Cased, Incoming, true) == EVerdict::Stage);
	}

	// --- The bundle layout moved ------------------------------------------------------------------
	// The terrain folder is renamed across bundle versions, and every served URL is built from the
	// manifest pointer. Either changing means the directory on disk is not the one this stream
	// would produce.
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

	// --- A layout this build does not know ---------------------------------------------------------
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

	// --- Round trip --------------------------------------------------------------------------------
	{
		FRecord Parsed;
		TestTrue(TEXT("a written record reads back"), FromJson(ToJson(Incoming), Parsed));
		TestEqual(TEXT("identity survives"), Parsed.Identity, Incoming.Identity);
		TestEqual(TEXT("digest survives"), Parsed.ManifestSha256, Incoming.ManifestSha256);
		TestEqual(TEXT("terrain prefix survives"), Parsed.TerrainPrefix, Incoming.TerrainPrefix);
		TestEqual(TEXT("terrain pointer survives"), Parsed.CesiumTerrainPath, Incoming.CesiumTerrainPath);
		TestEqual(TEXT("entry count survives"), Parsed.EntryCount, Incoming.EntryCount);
		TestEqual(TEXT("scheme survives"), Parsed.SchemeVersion, Incoming.SchemeVersion);

		// And the round trip is what the reuse decision is actually made on.
		TestTrue(TEXT("a round-tripped record still reuses"),
			Classify(true, Parsed, Incoming, true) == EVerdict::Reuse);
	}

	// --- Unreadable records are absent, not trusted --------------------------------------------------
	{
		FRecord Parsed;
		TestFalse(TEXT("nonsense is not a record"), FromJson(TEXT("not json at all"), Parsed));
		TestFalse(TEXT("an empty string is not a record"), FromJson(FString(), Parsed));
		TestFalse(TEXT("a record naming no bundle is not a record"),
			FromJson(TEXT("{\"entry_count\":42}"), Parsed));
		TestFalse(TEXT("an empty identity is not a record"),
			FromJson(TEXT("{\"identity\":\"\"}"), Parsed));
	}

	// --- Where the two paths sit relative to each other -----------------------------------------------
	// The record is a SIBLING of the staging directory, never a file inside it: that directory is
	// served over HTTP, so a bookkeeping file inside it would be servable content.
	{
		const FString Identity = TEXT("ord-11112222-3333");
		const FString Dir = StagingDir(Identity);
		const FString Record = RecordPath(Identity);

		TestTrue(TEXT("the staging directory is named by the FULL identity, not the truncation"),
			Dir.EndsWith(TEXT("/") + Identity));
		TestFalse(TEXT("the record is not inside the served directory"),
			Record.StartsWith(Dir + TEXT("/")));
		TestTrue(TEXT("the record sits beside it"), Record.EndsWith(Identity + TEXT(".json")));

		// Two identities that share their first eight characters must not share a directory. In the
		// content tree they deliberately do, guarded by the provenance record; here there is nothing
		// to trade readability for, and the availability rewrite makes a collision serve wrong tiles.
		TestNotEqual(TEXT("a short-form collision does not collide here"),
			StagingDir(TEXT("ord-1111aaaa")), StagingDir(TEXT("ord-1111bbbb")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
