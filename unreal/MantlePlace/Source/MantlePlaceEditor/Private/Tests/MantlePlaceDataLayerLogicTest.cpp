// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceDataLayerLogic.h"
#include "MantlePlaceImportNaming.h"

// The Data Layer decisions, asserted without a world: which family each artifact declares, what a
// family's layer is called and where its asset lives, and that nothing is assigned outside World
// Partition. The world-side round trip is MantlePlace.Import.WorldPartition.DataLayers.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceDataLayerLogicTest,
	"MantlePlace.Import.DataLayer.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceDataLayerLogicTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceDataLayerLogic;

	// --- Every handler declares its family, and the families are the ones the bundle is layered in -
	{
		TMap<FName, EMantlePlaceLayerFamily> Expected;
		Expected.Add(FName(TEXT("drape")), EMantlePlaceLayerFamily::Terrain);
		Expected.Add(MantlePlaceArtifactId::Landscape(), EMantlePlaceLayerFamily::Terrain);
		Expected.Add(FName(TEXT("coverage_rasters")), EMantlePlaceLayerFamily::Coverage);
		Expected.Add(MantlePlaceArtifactId::TerrainMesh(), EMantlePlaceLayerFamily::Terrain);
		Expected.Add(FName(TEXT("buildings")), EMantlePlaceLayerFamily::Buildings);
		Expected.Add(FName(TEXT("road_splines")), EMantlePlaceLayerFamily::Roads);
		Expected.Add(FName(TEXT("foliage_points")), EMantlePlaceLayerFamily::Foliage);
		Expected.Add(MantlePlaceArtifactId::FoliageScatter(), EMantlePlaceLayerFamily::Foliage);
		Expected.Add(FName(TEXT("public_records")), EMantlePlaceLayerFamily::PublicRecords);

		const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
		TestEqual(TEXT("the expectation covers every registered handler"), Handlers.Num(), Expected.Num());
		for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
		{
			const EMantlePlaceLayerFamily* Want = Expected.Find(Handler->Id());
			if (TestNotNull(*FString::Printf(TEXT("%s has an expected family"), *Handler->Id().ToString()), Want))
			{
				TestTrue(*FString::Printf(TEXT("%s declares its family"), *Handler->Id().ToString()),
					Handler->LayerFamily() == *Want);
			}
		}
	}

	// --- Names: one per family, distinct, and none for None --------------------------------------
	{
		TSet<FString> Names;
		for (const EMantlePlaceLayerFamily Family : AssignableFamilies())
		{
			const FString Name = FamilyName(Family);
			TestFalse(TEXT("every assignable family has a name"), Name.IsEmpty());
			Names.Add(Name);
		}
		TestEqual(TEXT("the family names are distinct"), Names.Num(), AssignableFamilies().Num());
		TestTrue(TEXT("None has no name"), FamilyName(EMantlePlaceLayerFamily::None).IsEmpty());
		TestFalse(TEXT("None is not assignable"), AssignableFamilies().Contains(EMantlePlaceLayerFamily::None));
	}

	// --- Assignment happens in World Partition only, and only for a real family ------------------
	{
		TestTrue(TEXT("a partitioned level assigns a family"), ShouldAssign(true, EMantlePlaceLayerFamily::Buildings));
		TestFalse(TEXT("a non-partitioned level assigns nothing"), ShouldAssign(false, EMantlePlaceLayerFamily::Buildings));
		TestFalse(TEXT("None is never assigned"), ShouldAssign(true, EMantlePlaceLayerFamily::None));
	}

	// --- Scoping: per family, shared across orders, out of every order's force-deleted folder ------
	{
		using namespace MantlePlaceImportNaming;
		const FString Root = TEXT("/Game/MantlePlace");
		const FString Family = FamilyName(EMantlePlaceLayerFamily::Buildings);
		TestEqual(TEXT("a Data Layer asset is DL_<family>"), DataLayerAssetName(Family), FString(TEXT("DL_Buildings")));

		const FString Package = DataLayerPackageName(Root, Family);
		TestTrue(TEXT("the layer lives under the content root"), Package.StartsWith(Root + TEXT("/")));
		TestTrue(TEXT("the layer's package ends in its asset name"), Package.EndsWith(TEXT("/DL_Buildings")));
		for (const TCHAR* Identity : { TEXT("aaaaaaaa-1111"), TEXT("bbbbbbbb-2222") })
		{
			TestFalse(*FString::Printf(TEXT("the layer is outside %s's force-deleted folder"), Identity),
				Package.StartsWith(ImportRoot(Root, Identity) + TEXT("/")));
		}
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
