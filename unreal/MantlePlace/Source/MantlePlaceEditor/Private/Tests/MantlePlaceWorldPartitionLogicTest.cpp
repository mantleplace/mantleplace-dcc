// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceWorldPartitionLogic.h"

// The decisions an import into a World Partition level makes, asserted without a world: how many
// streaming proxies a split yields, the order a wipe destroys in, which actors are kept always
// loaded, when a re-import loads the prior import's unloaded actors to wipe them and when it must
// refuse instead (one it cannot load) rather than leave part of the prior import behind,
// including the foliage a prior scatter generated onto PCG partition actors in cells now unloaded,
// and not when the only "unloaded" actors are ones this session destroyed and has not saved since.

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

	// --- v2: the wipe loads them first; the refusal is only the fallback for one it cannot load ----
	{
		TestTrue(TEXT("nothing unloaded loads nothing and is no refusal"), ExplainUnloadablePriorActors(0, 0).IsEmpty());
		TestTrue(TEXT("every unloaded prior actor loaded is no refusal"), ExplainUnloadablePriorActors(5, 5).IsEmpty());
		const FString OneShort = ExplainUnloadablePriorActors(5, 4);
		TestFalse(TEXT("one the wipe could not load refuses the whole re-import"), OneShort.IsEmpty());
		TestEqual(TEXT("with v1's message, counting the ones it could not load"), OneShort, ExplainUnloadedPriorActors(1));
		TestEqual(TEXT("none loaded refuses them all"), ExplainUnloadablePriorActors(3, 0), ExplainUnloadedPriorActors(3));
		TestTrue(TEXT("a count loaded above the count found is no refusal, never a negative count"),
			ExplainUnloadablePriorActors(2, 3).IsEmpty());
	}

	// --- Unloaded PCG cells: a prior scatter's instances live on untagged partition actors ---------
	{
		// A 400 m AOI about the origin, and 256 m PCG cells: the four cells about the origin hold it.
		const TArray<FBox2D> Scatter = { FBox2D(FVector2D(-20000.0, -20000.0), FVector2D(20000.0, 20000.0)) };
		const FBox2D Inside(FVector2D(0.0, 0.0), FVector2D(25600.0, 25600.0));
		const FBox2D Beyond(FVector2D(25600.0, 0.0), FVector2D(51200.0, 25600.0));
		const FBox2D Touching(FVector2D(20000.0, -5000.0), FVector2D(45600.0, 5000.0));
		TestTrue(TEXT("a cell overlapping the scatter's volume holds part of it"), CellHoldsPriorScatter(Inside, Scatter));
		TestFalse(TEXT("a cell beyond the volume holds none of it"), CellHoldsPriorScatter(Beyond, Scatter));
		TestFalse(TEXT("a cell that only touches the volume's edge holds none of it"), CellHoldsPriorScatter(Touching, Scatter));
		TestFalse(TEXT("no prior scatter, so no cell holds one"), CellHoldsPriorScatter(Inside, TArray<FBox2D>()));
		TestFalse(TEXT("a cell with no bounds cannot be placed, so it is not counted"),
			CellHoldsPriorScatter(FBox2D(ForceInit), Scatter));
		const TArray<FBox2D> Two = { FBox2D(FVector2D(90000.0, 90000.0), FVector2D(91000.0, 91000.0)), Scatter[0] };
		TestTrue(TEXT("any one of several prior scatters is enough"), CellHoldsPriorScatter(Inside, Two));
	}

	// --- Stale descriptors: an actor this session destroyed is not an unloaded one ----------------
	{
		FDescriptorPackage Destroyed;
		Destroyed.bInMemory = true;
		Destroyed.bDirty = true;
		Destroyed.bHoldsLiveActor = false;
		TestTrue(TEXT("a dirty package in memory with no live actor is a destroyed actor's, awaiting the save that deletes it"),
			IsDestroyedThisSession(Destroyed));

		TestFalse(TEXT("a package not in memory is a really unloaded actor's"),
			IsDestroyedThisSession(FDescriptorPackage()));

		FDescriptorPackage Unloaded = Destroyed;
		Unloaded.bDirty = false;
		TestFalse(TEXT("an unchanged package in memory is an actor unloaded with its region, not destroyed"),
			IsDestroyedThisSession(Unloaded));

		FDescriptorPackage Live = Destroyed;
		Live.bHoldsLiveActor = true;
		TestFalse(TEXT("a package still holding a live actor was not emptied by a destroy"),
			IsDestroyedThisSession(Live));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
