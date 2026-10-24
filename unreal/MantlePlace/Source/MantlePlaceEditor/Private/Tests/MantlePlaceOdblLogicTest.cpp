// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceOdblLogic.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "Containers/StringConv.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

// The ODbL decisions, asserted without a world or a bundle: which role a file reads as (a missing or
// unknown one is the stricter `database`), which content owes the credit and what it says, what
// Prepare for handover does to each piece, and that the manifest's `file_entries` and
// `attribution.sources` reach the logic. The world-side round trip is
// MantlePlace.Import.Odbl.Handover.

namespace MantlePlaceOdblLogicTest
{
FMantlePlaceFileLicensing Entry(EMantlePlaceOdblRole Role, const TCHAR* SourceLicense = nullptr)
{
	FMantlePlaceFileLicensing Result;
	Result.Role = Role;
	if (SourceLicense != nullptr)
	{
		FMantlePlaceFileSource Source;
		Source.Name = TEXT("OpenStreetMap");
		Source.License = SourceLicense;
		Result.Sources.Add(Source);
	}
	return Result;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOdblLogicTest,
	"MantlePlace.Import.Odbl.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOdblLogicTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceOdblLogic;
	using MantlePlaceOdblLogicTest::Entry;

	// --- A role: stated ones read as stated, everything else is the stricter duty ------------------
	TestTrue(TEXT("database"), ParseRole(true, TEXT("database")) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("produced-work"), ParseRole(true, TEXT("produced-work")) == EMantlePlaceOdblRole::ProducedWork);
	TestTrue(TEXT("none"), ParseRole(true, TEXT("none")) == EMantlePlaceOdblRole::None);
	TestTrue(TEXT("a missing role reads as database"), ParseRole(false, FString()) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("a role this reader has not met reads as database"),
		ParseRole(true, TEXT("collective-database")) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("a stated but empty role reads as database"), ParseRole(true, FString()) == EMantlePlaceOdblRole::Database);
	TestEqual(TEXT("the published words"), RoleName(EMantlePlaceOdblRole::ProducedWork), FString(TEXT("produced-work")));

	// --- A file and a set of files -----------------------------------------------------------------
	TMap<FString, FMantlePlaceFileLicensing> Entries;
	Entries.Add(TEXT("Mesh/Terrain.glb"), Entry(EMantlePlaceOdblRole::ProducedWork));
	Entries.Add(TEXT("Landcover/WaterMask.png"), Entry(EMantlePlaceOdblRole::None));
	Entries.Add(TEXT("Mesh/Buildings.glb"), Entry(EMantlePlaceOdblRole::ProducedWork, TEXT("ODbL-1.0")));
	Entries.Add(TEXT("Vector/RoadSplines.gpkg"), Entry(EMantlePlaceOdblRole::Database, TEXT("ODbL-1.0")));

	TestTrue(TEXT("a file with an entry reads as its role"),
		RoleOfPath(Entries, TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::ProducedWork);
	TestTrue(TEXT("a file with no entry reads as database"),
		RoleOfPath(Entries, TEXT("Vector/Unlisted.geojson")) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("no entries at all: every file is database"),
		RoleOfPath(TMap<FString, FMantlePlaceFileLicensing>(), TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("no files is none: there is no file whose role could be missing"),
		RoleOfPaths(Entries, {}) == EMantlePlaceOdblRole::None);
	TestTrue(TEXT("one database file makes the set database"),
		RoleOfPaths(Entries, { TEXT("Mesh/Terrain.glb"), TEXT("Vector/RoadSplines.gpkg") }) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("one unlisted file makes the set database"),
		RoleOfPaths(Entries, { TEXT("Mesh/Terrain.glb"), TEXT("Unlisted.bin") }) == EMantlePlaceOdblRole::Database);
	TestTrue(TEXT("produced-work and none together read as produced-work"),
		RoleOfPaths(Entries, { TEXT("Landcover/WaterMask.png"), TEXT("Mesh/Terrain.glb") }) == EMantlePlaceOdblRole::ProducedWork);
	TestTrue(TEXT("none alone reads as none"),
		RoleOfPaths(Entries, { TEXT("Landcover/WaterMask.png") }) == EMantlePlaceOdblRole::None);

	// --- The credit --------------------------------------------------------------------------------
	TestTrue(TEXT("a database file owes the credit"), NeedsCredit(Entries, { TEXT("Vector/RoadSplines.gpkg") }));
	TestTrue(TEXT("a Produced Work from an ODbL source still owes it"), NeedsCredit(Entries, { TEXT("Mesh/Buildings.glb") }));
	TestFalse(TEXT("a Produced Work from no ODbL source does not"), NeedsCredit(Entries, { TEXT("Mesh/Terrain.glb") }));
	TestFalse(TEXT("nor does a file no ODbL source touched"), NeedsCredit(Entries, { TEXT("Landcover/WaterMask.png") }));
	TestFalse(TEXT("nor does an artifact made from no file"), NeedsCredit(Entries, {}));

	FMantlePlaceAttributionSource Osm;
	Osm.ProviderId = TEXT("openstreetmap");
	Osm.AttributionText = TEXT("(c) OpenStreetMap contributors");
	Osm.License = TEXT("ODbL-1.0");
	Osm.LicenseUrl = TEXT("https://www.openstreetmap.org/copyright");
	const FString Dash = FString::Printf(TEXT(" %c "), static_cast<TCHAR>(0x2014));
	TestEqual(TEXT("a credit line is the manifest's words, joined"), CreditLine(Osm),
		TEXT("(c) OpenStreetMap contributors") + Dash + TEXT("ODbL-1.0") + Dash + TEXT("https://www.openstreetmap.org/copyright"));
	FMantlePlaceAttributionSource Bare;
	Bare.ProviderId = TEXT("usgs-3dep");
	Bare.AttributionText = TEXT("  ");
	TestEqual(TEXT("a source with no attribution text is named by its provider id"), CreditLine(Bare), FString(TEXT("usgs-3dep")));
	FMantlePlaceAttributionSource Wrapped;
	Wrapped.AttributionText = TEXT("Line one\r\nline two");
	TestEqual(TEXT("a line break inside a field is a space: one source is one line"),
		CreditLine(Wrapped), FString(TEXT("Line one line two")));

	const TArray<FString> FromAttribution = CreditLines({ Osm, Osm }, Entries, { TEXT("Vector/RoadSplines.gpkg") });
	TestEqual(TEXT("the manifest's attribution is the credit, deduped"), FromAttribution.Num(), 1);
	const TArray<FString> FromSources = CreditLines({}, Entries, { TEXT("Mesh/Buildings.glb"), TEXT("Mesh/Terrain.glb") });
	TestEqual(TEXT("with no attribution the ODbL sources of the files are the credit"), FromSources.Num(), 1);
	TestEqual(TEXT("named and licensed"), FromSources.Num() == 1 ? FromSources[0] : FString(),
		FString(TEXT("OpenStreetMap")) + Dash + TEXT("ODbL-1.0"));
	TestTrue(TEXT("nothing to credit gives no lines"), CreditLines({}, Entries, { TEXT("Mesh/Terrain.glb") }).IsEmpty());

	// --- Prepare for handover ----------------------------------------------------------------------
	const auto Item = [](const TCHAR* Identity, bool bOdbl, const TCHAR* Group, std::initializer_list<const TCHAR*> Credit)
	{
		FHandoverItem Result;
		Result.Identity = Identity;
		Result.bOdbl = bOdbl;
		Result.EquivalenceGroup = Group;
		for (const TCHAR* Line : Credit)
		{
			Result.CreditLines.Add(Line);
		}
		return Result;
	};
	{
		const TArray<FHandoverItem> Items = {
			Item(TEXT("a"), true, TEXT("terrain"), { TEXT("OSM") }),   // 0 landscape: a Produced Work mesh stands in
			Item(TEXT("a"), false, TEXT("terrain"), {}),               // 1 mesh: kept
			Item(TEXT("a"), true, TEXT(""), { TEXT("OSM") }),          // 2 roads: nothing stands in
			Item(TEXT("a"), false, TEXT(""), {}),                      // 3 terrain imagery: kept
			Item(TEXT("b"), true, TEXT("terrain"), { TEXT("OSM") }),   // 4 another order's landscape: its own mesh is absent
			Item(TEXT("c"), false, TEXT(""), {}),                      // 5 an order that owes no credit
		};
		const FHandoverPlan Plan = PlanHandover(Items);
		if (TestEqual(TEXT("a decision per item"), Plan.Decisions.Num(), Items.Num()))
		{
			TestTrue(TEXT("ODbL with a Produced Work of its group in the same order is swapped"),
				Plan.Decisions[0] == EHandoverDecision::Swap);
			TestTrue(TEXT("the Produced Work is kept"), Plan.Decisions[1] == EHandoverDecision::Keep);
			TestTrue(TEXT("ODbL with no group is removed"), Plan.Decisions[2] == EHandoverDecision::Remove);
			TestTrue(TEXT("what is not ODbL is kept"), Plan.Decisions[3] == EHandoverDecision::Keep);
			TestTrue(TEXT("another order's Produced Work is no stand-in: removed"), Plan.Decisions[4] == EHandoverDecision::Remove);
			TestTrue(TEXT("an order with nothing ODbL is untouched"), Plan.Decisions[5] == EHandoverDecision::Keep);
		}
		TestEqual(TEXT("a credit for each order that owes one, and no other"), Plan.Credits.Num(), 2);
		if (Plan.Credits.Num() == 2)
		{
			TestEqual(TEXT("in first-seen order"), Plan.Credits[0].Identity, FString(TEXT("a")));
			TestEqual(TEXT("with its lines once"), Plan.Credits[0].Lines.Num(), 1);
			TestEqual(TEXT("the second order"), Plan.Credits[1].Identity, FString(TEXT("b")));
		}
	}
	{
		const TArray<FHandoverItem> Items = { Item(TEXT("a"), false, TEXT(""), { TEXT("OSM") }) };
		const FHandoverPlan Plan = PlanHandover(Items);
		TestTrue(TEXT("a Produced Work from an ODbL source is kept"), Plan.Decisions[0] == EHandoverDecision::Keep);
		TestEqual(TEXT("and still carries the credit"), Plan.Credits.Num(), 1);
	}
	TestTrue(TEXT("an empty level plans nothing"), PlanHandover({}).Decisions.IsEmpty() && PlanHandover({}).Credits.IsEmpty());
	TestEqual(TEXT("credit text is one line per source"), CreditText({ TEXT("one"), TEXT("two") }), FString(TEXT("one\ntwo")));

	// --- Names: the ODbL folder is the order's, the tags are the ones Prepare for handover reads ----
	const FString Identity = TEXT("aaaaaaaa-1111");
	TestEqual(TEXT("the ODbL folder sits inside the order's own"),
		MantlePlaceImportNaming::OdblFolder(Identity), MantlePlaceImportNaming::OutlinerFolder(Identity) + TEXT("/ODbL"));
	TestTrue(TEXT("the ODbL tag is not the import tag"),
		MantlePlaceImportNaming::OdblTag() != FName(*MantlePlaceImportNaming::ImportTag(Identity)));
	TestTrue(TEXT("the credit actor is not an import's: it carries no import tag, so a handover never removes it"),
		!MantlePlaceImportNaming::CreditActorTag(Identity).StartsWith(MantlePlaceImportNaming::ImportTagPrefix()));
	TestTrue(TEXT("the credit-line tag is not the credit-actor tag"),
		!MantlePlaceImportNaming::CreditActorTag(Identity).StartsWith(MantlePlaceImportNaming::CreditLineTagPrefix()));
	TestTrue(TEXT("a credit part is neither a whole credit line nor the credit actor"),
		!MantlePlaceImportNaming::CreditPartTagPrefix().StartsWith(MantlePlaceImportNaming::CreditLineTagPrefix())
			&& !MantlePlaceImportNaming::CreditActorTag(Identity).StartsWith(MantlePlaceImportNaming::CreditPartTagPrefix()));
	TestEqual(TEXT("the ODbL layer's asset"), MantlePlaceImportNaming::DataLayerAssetName(MantlePlaceImportNaming::OdblLayerName()),
		FString(TEXT("DL_ODbL")));

	return true;
}

// The manifest reaches the logic: `file_entries` and `attribution.sources` are read from the shared
// corpus, and the two cases the standard singles out come out as the stricter duty.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOdblManifestTest,
	"MantlePlace.Import.Odbl.Manifest",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOdblManifestTest::RunTest(const FString& Parameters)
{
	const FString Root = MantlePlaceConformanceCorpus::FindCorpusRoot();
	if (!TestFalse(TEXT("the shared corpus is found"), Root.IsEmpty()))
	{
		return false;
	}
	const auto Read = [&](const TCHAR* File, FMantlePlaceVaultManifest& Out)
	{
		FString Text;
		FString Error;
		if (!FFileHelper::LoadFileToString(Text, *FPaths::Combine(Root, TEXT("manifest"), File)))
		{
			AddError(FString::Printf(TEXT("could not read corpus file %s"), File));
			return false;
		}
		Out = MantlePlaceImportManifest::Parse(Text, Error);
		return TestTrue(*FString::Printf(TEXT("%s parses: %s"), File, *Error), Out.bValid);
	};

	// A role stated on the road layer, beside a terrain mesh that says produced-work.
	{
		FMantlePlaceVaultManifest M;
		if (Read(TEXT("file-frames-ignored.json"), M))
		{
			TestTrue(TEXT("file_entries are read"), M.FileLicensing.Num() > 0);
			TestTrue(TEXT("the terrain mesh states produced-work"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::ProducedWork);
			const FMantlePlaceFileLicensing* Mesh = M.FileLicensing.Find(TEXT("Mesh/Terrain.glb"));
			TestTrue(TEXT("with its licence and its source"), Mesh != nullptr && !Mesh->License.IsEmpty() && Mesh->Sources.Num() == 1);
		}
	}
	// The credit the manifest words, read from its own block.
	{
		FMantlePlaceVaultManifest M;
		if (Read(TEXT("bundle-parts-ignored.json"), M) && TestEqual(TEXT("attribution.sources are read"), M.Attribution.Num(), 1))
		{
			TestEqual(TEXT("verbatim"), M.Attribution[0].AttributionText, FString(TEXT("U.S. Geological Survey, 3D Elevation Program")));
			TestEqual(TEXT("with its provider"), M.Attribution[0].ProviderId, FString(TEXT("usgs-3dep")));
		}
	}
	// The roleless road layer reads as database; the mesh keeps what it states.
	{
		FMantlePlaceVaultManifest M;
		if (Read(TEXT("file-entry-no-role.json"), M))
		{
			TestTrue(TEXT("a roleless entry reads as database, never none"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Vector/RoadSplines.geojson")) == EMantlePlaceOdblRole::Database);
			TestTrue(TEXT("and its stated licence is kept"),
				M.FileLicensing.FindRef(TEXT("Vector/RoadSplines.geojson")).License == TEXT("ODbL-1.0"));
			TestTrue(TEXT("the mesh keeps its role"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::ProducedWork);
		}
	}
	// A role this reader has not met fails closed, without refusing the manifest.
	{
		FMantlePlaceVaultManifest M;
		if (Read(TEXT("file-entry-unknown-role.json"), M))
		{
			TestTrue(TEXT("an unknown role reads as database"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Vector/RoadSplines.geojson")) == EMantlePlaceOdblRole::Database);
			TestTrue(TEXT("the mesh's known role is unaffected"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::ProducedWork);
		}
	}
	// A bundle from before the member: nothing is listed, so everything reads as database.
	{
		FMantlePlaceVaultManifest M;
		if (Read(TEXT("full.json"), M))
		{
			TestTrue(TEXT("no file_entries: nothing is listed"), M.FileLicensing.IsEmpty());
			TestTrue(TEXT("so every file reads as database"),
				MantlePlaceOdblLogic::RoleOfPath(M.FileLicensing, TEXT("Mesh/Terrain.glb")) == EMantlePlaceOdblRole::Database);
		}
	}
	return true;
}

// A credit line is recorded on the actor as tags, and a tag is an FName, which holds at most 1023
// characters: a longer FName is an assertion that takes the editor down. A real bundle's attribution
// carries lines far longer than that (a public-records disclaimer of 1,750 characters crashed the
// first live import of a 2.0 bundle), so a line that does not fit one tag is recorded in numbered
// parts and read back whole, verbatim and in order.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOdblCreditTagsTest,
	"MantlePlace.Import.Odbl.CreditTags",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOdblCreditTagsTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceOdblLogic;

	const FString Short = TEXT("(c) OpenStreetMap contributors — ODbL-1.0");
	FString Long;
	for (int32 Index = 0; Long.Len() < 2500; ++Index)
	{
		Long += FString::Printf(TEXT("Clause %d of a disclaimer that is not legal advice. "), Index);
	}
	const FString AlsoShort = TEXT("USGS 3DEP 1 m DEM");

	// A line that fits is the tag it always was, so a level imported before this reads the same.
	{
		const TArray<FString> Tags = CreditTags({ Short });
		TestEqual(TEXT("one short line is one tag"), Tags.Num(), 1);
		TestEqual(TEXT("in the form every earlier import wrote"),
			Tags.Num() > 0 ? Tags[0] : FString(), MantlePlaceImportNaming::CreditLineTag(Short));
	}

	// A long line becomes parts, every one short enough to be a tag, and reads back whole and in place.
	{
		const TArray<FString> Lines = { Short, Long, AlsoShort };
		const TArray<FString> Tags = CreditTags(Lines);
		TestTrue(TEXT("the long line is more than one tag"), Tags.Num() > Lines.Num());
		int32 Longest = 0;
		for (const FString& Tag : Tags)
		{
			Longest = FMath::Max(Longest, Tag.Len());
		}
		TestTrue(FString::Printf(TEXT("every tag fits an FName (longest %d)"), Longest), Longest <= NAME_SIZE - 1);
		TestEqual(TEXT("the lines read back verbatim, in order"), CreditLinesFromTags(Tags), Lines);
	}

	// Read back from an actor's tags, which hold other tags too and may hold the same line twice.
	{
		TArray<FString> Tags = { TEXT("mantleplace_import=abc"), TEXT("mantleplace_odbl") };
		Tags.Append(CreditTags({ Long }));
		Tags.Add(TEXT("mantleplace_equivalent=terrain"));
		Tags.Append(CreditTags({ Short }));
		TestEqual(TEXT("other tags are ignored"), CreditLinesFromTags(Tags), TArray<FString>({ Long, Short }));
	}

	// A line with a part missing (an edited tag list) is dropped rather than credited half-said.
	{
		TArray<FString> Tags = CreditTags({ Long, Short });
		Tags.RemoveAt(1);
		TestEqual(TEXT("an incomplete line is dropped, the rest kept"), CreditLinesFromTags(Tags), TArray<FString>({ Short }));
	}

	// A part never ends between the two halves of a surrogate pair.
	{
		FString Wide;
		while (Wide.Len() < 3000)
		{
			Wide += TEXT("🌍"); // a character outside the BMP: two UTF-16 units
		}
		const TArray<FString> Tags = CreditTags({ Wide });
		TestEqual(TEXT("a line of wide characters reads back verbatim"), CreditLinesFromTags(Tags), TArray<FString>({ Wide }));
		for (const FString& Tag : Tags)
		{
			TestFalse(TEXT("no part ends on a high surrogate"),
				!Tag.IsEmpty() && StringConv::IsHighSurrogate(Tag[Tag.Len() - 1]));
		}
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
