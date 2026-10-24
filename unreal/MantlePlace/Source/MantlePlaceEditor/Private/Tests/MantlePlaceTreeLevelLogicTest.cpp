// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceTreePointsLogic.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

// Which tree-points level an import reads, asserted without a bundle: the block's `levels` as
// the handler parses them, what ResolveLevel makes of each shape, and the row cut. Two shapes of
// bundle are covered side by side. A 2.0 bundle ships ONE ranked TreePoints.csv and states MED and
// MIN as `cut: {rows}` of it. An older bundle ships a file per level and states them as pointers,
// and keeps working.

namespace
{
const TCHAR* const Block2 = TEXT(R"({
  "path": "Landcover/TreePoints-Engine.csv", "sha256": "aa", "point_count": 100,
  "levels": {
    "RAW": { "path": "Landcover/TreePoints-Raw.csv" },
    "MAX": { "cost_driver": { "unit": "elements", "count": 100 } },
    "MED": { "cut": { "rows": 40 } },
    "MIN": { "same_as": "MED", "reason": "no_reduction" }
  } })");

const TCHAR* const BlockOlder = TEXT(R"({
  "path": "Landcover/TreePointsMetric.csv", "sha256": "aa", "point_count": 100,
  "levels": {
    "MAX": {},
    "MED": { "path": "Landcover/TreePointsMetric-Med.csv", "sha256": "bb", "point_count": 40,
             "crs": "EPSG:32613", "units": "m", "horizontal_units": "m" },
    "MIN": { "same_as": "MAX", "reason": "max_only" }
  } })");

FMantlePlaceFoliagePointsBlock ParseBlock(const TCHAR* BlockJson)
{
	const FString Text = FString::Printf(TEXT("{ \"hosts\": { \"unreal\": { \"foliage_points\": %s } } }"), BlockJson);
	TSharedPtr<FJsonObject> Root;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
	FJsonSerializer::Deserialize(Reader, Root);

	FMantlePlaceVaultManifest Manifest;
	if (!Root.IsValid())
	{
		return Manifest.Foliage;
	}
	const TSharedPtr<FJsonObject>* Hosts = nullptr;
	const TSharedPtr<FJsonObject>* UnrealPtr = nullptr;
	if (Root->TryGetObjectField(TEXT("hosts"), Hosts) && (*Hosts)->TryGetObjectField(TEXT("unreal"), UnrealPtr))
	{
		const FMantlePlaceParseInput In{ Root, *UnrealPtr };
		const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
		FString Error;
		for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
		{
			if (Handler->Id() == FName(TEXT("foliage_points")))
			{
				Handler->ParseFields(In, Manifest, Error);
			}
		}
	}
	return Manifest.Foliage;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceTreeLevelLogicTest,
	"MantlePlace.Import.TreeLevelLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTreeLevelLogicTest::RunTest(const FString& Parameters)
{
	using Logic = FMantlePlaceTreePointsLogic;

	// --- A 2.0 bundle: one ranked file, MED a row cut of it -----------------------------------
	{
		const FMantlePlaceFoliagePointsBlock Block = ParseBlock(Block2);
		TestEqual(TEXT("MED parses as a row cut"), (int32)Block.LevelMed.Form, (int32)EMantlePlaceTreeLevelForm::RowCut);
		TestEqual(TEXT("the cut's row count is read"), Block.LevelMed.Rows, 40);
		TestEqual(TEXT("MIN parses as same_as"), (int32)Block.LevelMin.Form, (int32)EMantlePlaceTreeLevelForm::SameAs);

		const FMantlePlaceResolvedTreeLevel Max = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Max);
		TestEqual(TEXT("MAX reads the block's file"), Max.Path, FString(TEXT("Landcover/TreePoints-Engine.csv")));
		TestEqual(TEXT("MAX takes every row"), Max.RowLimit, 0);
		TestEqual(TEXT("MAX is checked against the block's count"), Max.DeclaredCount, 100);

		const FMantlePlaceResolvedTreeLevel Med = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Med);
		TestEqual(TEXT("MED reads the SAME file as MAX"), Med.Path, Max.Path);
		TestEqual(TEXT("MED is verified against the same digest"), Med.Sha256, Max.Sha256);
		TestEqual(TEXT("MED takes the first 40 rows"), Med.RowLimit, 40);
		TestEqual(TEXT("MED is checked against 40 rows"), Med.DeclaredCount, 40);
		TestEqual(TEXT("MED is applied as MED"), (int32)Med.Applied, (int32)EMantlePlaceTreeLevel::Med);
		TestTrue(TEXT("a level that exists has no fallback reason"), Med.FallbackReason.IsEmpty());

		const FMantlePlaceResolvedTreeLevel Min = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Min);
		TestEqual(TEXT("MIN same_as MED reads MED's cut"), Min.RowLimit, 40);
		TestEqual(TEXT("MIN same_as MED is applied as MED"), (int32)Min.Applied, (int32)EMantlePlaceTreeLevel::Med);
	}

	// --- An older bundle: a file per level, kept ----------------------------------------------
	{
		const FMantlePlaceFoliagePointsBlock Block = ParseBlock(BlockOlder);
		TestEqual(TEXT("MED parses as a pointer"), (int32)Block.LevelMed.Form, (int32)EMantlePlaceTreeLevelForm::Pointer);

		const FMantlePlaceResolvedTreeLevel Med = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Med);
		TestEqual(TEXT("MED reads its own file"), Med.Path, FString(TEXT("Landcover/TreePointsMetric-Med.csv")));
		TestEqual(TEXT("MED is verified against its own digest"), Med.Sha256, FString(TEXT("bb")));
		TestEqual(TEXT("MED takes every row of its file"), Med.RowLimit, 0);
		TestEqual(TEXT("MED is checked against its own count"), Med.DeclaredCount, 40);
		TestTrue(TEXT("MED states its own frame"), Med.bOwnFrame);
		TestEqual(TEXT("MED's CRS is its own"), Med.Crs, FString(TEXT("EPSG:32613")));

		const FMantlePlaceResolvedTreeLevel Min = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Min);
		TestEqual(TEXT("MIN same_as MAX reads the block's file"), Min.Path, FString(TEXT("Landcover/TreePointsMetric.csv")));
		TestEqual(TEXT("MIN same_as MAX is applied as MAX"), (int32)Min.Applied, (int32)EMantlePlaceTreeLevel::Max);
	}

	// --- A bundle with no `levels`, or a level it does not publish, reads MAX and says so -----
	{
		const FMantlePlaceFoliagePointsBlock Block = ParseBlock(TEXT(R"({ "path": "Landcover/TreePoints.csv", "point_count": 7 })"));
		const FMantlePlaceResolvedTreeLevel Med = Logic::ResolveLevel(Block, EMantlePlaceTreeLevel::Med);
		TestEqual(TEXT("an unpublished level reads the block's file"), Med.Path, FString(TEXT("Landcover/TreePoints.csv")));
		TestEqual(TEXT("an unpublished level is applied as MAX"), (int32)Med.Applied, (int32)EMantlePlaceTreeLevel::Max);
		TestFalse(TEXT("and says why"), Med.FallbackReason.IsEmpty());
		TestEqual(TEXT("no row is cut"), Med.RowLimit, 0);
	}
	{
		// A chain, a cut of an unknown form and an unknown level name all fail closed to MAX.
		const FMantlePlaceFoliagePointsBlock Chained = ParseBlock(TEXT(R"({ "path": "T.csv", "levels": {
		    "MED": { "same_as": "MIN" }, "MIN": { "same_as": "MED" } } })"));
		TestFalse(TEXT("a chained same_as is unavailable"),
			Logic::ResolveLevel(Chained, EMantlePlaceTreeLevel::Med).FallbackReason.IsEmpty());
		const FMantlePlaceFoliagePointsBlock Field = ParseBlock(TEXT(R"({ "path": "T.csv", "levels": {
		    "MED": { "cut": { "lowest_level_field": "level" } } } })"));
		TestFalse(TEXT("a cut by a form this build does not know is unavailable"),
			Logic::ResolveLevel(Field, EMantlePlaceTreeLevel::Med).FallbackReason.IsEmpty());
		const FMantlePlaceFoliagePointsBlock Raw = ParseBlock(TEXT(R"({ "path": "T.csv", "levels": {
		    "MED": { "same_as": "RAW" } } })"));
		TestFalse(TEXT("same_as RAW is not imported"),
			Logic::ResolveLevel(Raw, EMantlePlaceTreeLevel::Med).FallbackReason.IsEmpty());
	}

	// --- The row cut: the header and the first N data rows -------------------------------------
	{
		const FString Csv = TEXT("x,y,ground_z\n1,1,1\n2,2,2\n\n3,3,3\n4,4,4\n");
		TestEqual(TEXT("two rows"), Logic::CutRows(Csv, 2), FString(TEXT("x,y,ground_z\n1,1,1\n2,2,2")));
		TestEqual(TEXT("blank lines are not rows"), Logic::CutRows(Csv, 3), FString(TEXT("x,y,ground_z\n1,1,1\n2,2,2\n\n3,3,3")));
		TestEqual(TEXT("a cut past the end keeps everything"), Logic::CutRows(Csv, 99), Csv);
		TestEqual(TEXT("no limit keeps everything"), Logic::CutRows(Csv, 0), Csv);
		TestEqual(TEXT("CRLF files cut the same"),
			Logic::CutRows(TEXT("x,y\r\n1,1\r\n2,2\r\n3,3\r\n"), 1), FString(TEXT("x,y\r\n1,1")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
