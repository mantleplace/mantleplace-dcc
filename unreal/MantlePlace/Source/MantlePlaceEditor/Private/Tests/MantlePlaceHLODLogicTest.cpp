// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceImportNaming.h"

// The HLOD decisions, asserted without a world: which families get an HLOD layer, of which type,
// what the layer is called and where it lives, when a build is refused, and what the import log
// says. The world-side round trip is MantlePlace.Import.WorldPartition.HLODs.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceHLODLogicTest,
	"MantlePlace.Import.HLOD.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceHLODLogicTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceHLODLogic;
	using Family = EMantlePlaceLayerFamily;

	// --- Families: landscape proxies (Terrain) and buildings, nothing else -----------------------
	{
		TestTrue(TEXT("Terrain gets an HLOD layer"), HasHLOD(Family::Terrain));
		TestTrue(TEXT("Buildings get an HLOD layer"), HasHLOD(Family::Buildings));
		for (const Family Other : { Family::None, Family::Coverage, Family::Roads, Family::Foliage, Family::PublicRecords })
		{
			TestFalse(*FString::Printf(TEXT("%s gets no HLOD layer"),
				*MantlePlaceDataLayerLogic::FamilyName(Other)), HasHLOD(Other));
		}
		TestEqual(TEXT("exactly two HLOD families"), HLODFamilies().Num(), 2);
	}

	// --- Assignment happens in World Partition only, and only for an HLOD family -------------------
	{
		TestTrue(TEXT("a partitioned level assigns Buildings"), ShouldAssign(true, Family::Buildings));
		TestFalse(TEXT("a non-partitioned level assigns nothing"), ShouldAssign(false, Family::Terrain));
		TestFalse(TEXT("Roads are never assigned"), ShouldAssign(true, Family::Roads));
	}

	// --- Type: simplified, since a bundle's meshes are few, large and unique ------------------------
	for (const Family HLODFamily : HLODFamilies())
	{
		TestTrue(TEXT("an HLOD family's layer is a simplified mesh"), LayerType(HLODFamily) == EHLODLayerType::MeshSimplify);
	}

	// --- Naming and scoping: HLOD_<family>, shared, beside the Data Layers -------------------------
	{
		using namespace MantlePlaceImportNaming;
		const FString Root = TEXT("/Game/MantlePlace");
		const FString Name = MantlePlaceDataLayerLogic::FamilyName(Family::Buildings);
		TestEqual(TEXT("an HLOD layer asset is HLOD_<family>"), HLODLayerAssetName(Name), FString(TEXT("HLOD_Buildings")));
		const FString Package = HLODLayerPackageName(Root, Name);
		TestEqual(TEXT("the HLOD layer sits beside the Data Layers"),
			FPaths::GetPath(Package), FPaths::GetPath(DataLayerPackageName(Root, Name)));
		TestFalse(TEXT("the HLOD layer is outside an order's force-deleted folder"),
			Package.StartsWith(ImportRoot(Root, TEXT("aaaaaaaa-1111")) + TEXT("/")));
	}

	// --- Build refusals: never a build that silently leaves the import out -------------------------
	{
		TestTrue(TEXT("a saved partitioned level builds"), ExplainBuildRefusal(true, true, 0).IsEmpty());
		TestTrue(TEXT("a non-partitioned level is refused"), ExplainBuildRefusal(false, true, 0).Contains(TEXT("not partitioned")));
		TestTrue(TEXT("a never-saved level is refused"), ExplainBuildRefusal(true, false, 0).Contains(TEXT("never been saved")));
		const FString Unsaved = ExplainBuildRefusal(true, true, 7);
		TestTrue(TEXT("unsaved HLOD source actors are refused, counted"), Unsaved.Contains(TEXT("7 actor(s)")));
		TestTrue(TEXT("and the refusal says what to do"), Unsaved.Contains(TEXT("File > Save All")));
	}

	// --- The import log: the cost, and the note naming both ways to build -------------------------
	{
		TestEqual(TEXT("the cost line"), DescribeBuildCost(12, 83.46), FString(TEXT("HLODs built: 12 HLOD actor(s) in 83.5 s.")));
		TestTrue(TEXT("no note outside World Partition"), PostImportNote(false, 5).IsEmpty());
		TestTrue(TEXT("no note when nothing has an HLOD layer"), PostImportNote(true, 0).IsEmpty());
		const FString Note = PostImportNote(true, 5);
		TestTrue(TEXT("the note names the editor's build"), Note.Contains(TEXT("Build > Build HLODs")));
		TestTrue(TEXT("the note names the explicit step"), Note.Contains(TEXT("BuildHLODs")));
		TestTrue(TEXT("the note says to save first"), Note.Contains(TEXT("File > Save All")));
		TestTrue(TEXT("the commandlet invocation names the HLOD builder"),
			CommandletArguments(TEXT("/Game/Maps/City")).StartsWith(
				TEXT("/Game/Maps/City -run=WorldPartitionBuilderCommandlet -Builder=WorldPartitionHLODsBuilder")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
