// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceWorldPartitionLogic.h"

// The decisions an import into a World Partition level makes, asserted without a world: how many
// streaming proxies a split yields, the order a wipe destroys in, which actors are kept always
// loaded, and when a re-import must refuse rather than leave part of the prior import behind.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionLogicTest,
	"MantlePlace.Import.WorldPartition.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionLogicTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionLogic;

	// --- Proxy count: the component grid divided by the grid size, per axis, rounded up -----------
	{
		TestEqual(TEXT("4x4 components on a grid of 2 is four proxies"), ProxyCount(4, 4, 2), 4);
		TestEqual(TEXT("a partial cell is still a proxy: 5x5 on 2 is nine"), ProxyCount(5, 5, 2), 9);
		TestEqual(TEXT("a grid of 1 is one proxy per component"), ProxyCount(3, 2, 1), 6);
		TestEqual(TEXT("a grid wider than the landscape is one proxy"), ProxyCount(4, 4, 16), 1);
		// The worst case the bundle can publish: 8129 squared over 32x32 components.
		TestEqual(TEXT("32x32 components on the engine default is 256 proxies"), ProxyCount(32, 32, 2), 256);
		TestEqual(TEXT("no components is no proxies"), ProxyCount(0, 4, 2), 0);
		TestEqual(TEXT("an unusable grid size yields no count rather than a division by zero"),
			ProxyCount(4, 4, 0), 0);
	}

	// --- Wipe order: streaming proxies before everything, the landscape parent last --------------
	{
		const TArray<EWipeRank> Ranks = {
			EWipeRank::LandscapeParent, EWipeRank::Other, EWipeRank::StreamingProxy,
			EWipeRank::Other, EWipeRank::StreamingProxy };
		const TArray<int32> Order = WipeOrder(Ranks);
		TestEqual(TEXT("every actor is destroyed exactly once"), Order.Num(), Ranks.Num());
		if (Order.Num() == 5)
		{
			TestEqual(TEXT("first proxy first"), Order[0], 2);
			TestEqual(TEXT("second proxy second"), Order[1], 4);
			TestEqual(TEXT("other actors keep their relative order"), Order[2], 1);
			TestEqual(TEXT("other actors keep their relative order (2)"), Order[3], 3);
			TestEqual(TEXT("the landscape parent goes last, after its proxies"), Order[4], 0);
		}
		TestEqual(TEXT("an empty wipe orders nothing"), WipeOrder(TArray<EWipeRank>()).Num(), 0);
	}

	// --- Spatial loading, v1: everything we spawn stays loaded, except the landscape family ------
	{
		TestFalse(TEXT("a non-partitioned level is left alone"),
			ShouldForceNonSpatiallyLoaded(/*bPartitioned*/ false, /*bStreamedFamily*/ false));
		TestTrue(TEXT("a mesh, spline or record actor in a partitioned level is kept loaded"),
			ShouldForceNonSpatiallyLoaded(true, false));
		TestFalse(TEXT("a landscape, its proxies and the HLOD families stream, so the proxies stream and HLODs stand in"),
			ShouldForceNonSpatiallyLoaded(true, true));
	}

	// --- Unloaded prior actors: a refusal, never a partial wipe -----------------------------------
	{
		TestTrue(TEXT("nothing unloaded is no refusal"), ExplainUnloadedPriorActors(0).IsEmpty());
		const FString One = ExplainUnloadedPriorActors(1);
		TestFalse(TEXT("one unloaded actor refuses"), One.IsEmpty());
		TestTrue(TEXT("the refusal says how many"), ExplainUnloadedPriorActors(7).Contains(TEXT("7")));
		TestTrue(TEXT("and what to do about it"), One.Contains(TEXT("Load")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
