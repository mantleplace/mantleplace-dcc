// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceLiveGateLogic.h"

// The decisions behind the live import test's placement gate, asserted headless: the unit factors it
// applies to a bundle's surface points, and the two statistics it judges the landscape by. The live
// test itself needs an editor and a real bundle; these do not. The motive is a US imperial order, whose
// `elevation.points_csv.units` is `ftUS` -- read as metres, every check point missed the landscape.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceLiveGateLogicTest,
    "MantlePlace.Import.LiveGateLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLiveGateLogicTest::RunTest(const FString& Parameters)
{
	// --- Unit factors ---------------------------------------------------------------------------
	{
		double Factor = 0.0;
		FString Reason;

		TestTrue(TEXT("m converts"), FMantlePlaceLiveGateLogic::TryUnitToMetres(TEXT("m"), Factor, Reason));
		TestEqual(TEXT("m is 1"), Factor, 1.0);

		Factor = 0.0;
		TestTrue(TEXT("an absent unit converts"), FMantlePlaceLiveGateLogic::TryUnitToMetres(FString(), Factor, Reason));
		TestEqual(TEXT("an absent unit means metres"), Factor, 1.0);

		TestTrue(TEXT("ftUS converts"), FMantlePlaceLiveGateLogic::TryUnitToMetres(TEXT("ftUS"), Factor, Reason));
		TestEqual(TEXT("ftUS is exactly 1200/3937 m"), Factor, 1200.0 / 3937.0);
		// 3937 US survey feet are exactly 1200 m: the factor must close that loop, not approximate it.
		TestTrue(TEXT("3937 ftUS is 1200 m"), FMath::IsNearlyEqual(3937.0 * Factor, 1200.0, 1e-9));
		TestTrue(TEXT("ftUS is not the international foot"), !FMath::IsNearlyEqual(Factor, 0.3048, 1e-9));

		TestTrue(TEXT("ft converts"), FMantlePlaceLiveGateLogic::TryUnitToMetres(TEXT("ft"), Factor, Reason));
		TestEqual(TEXT("ft is exactly 0.3048 m"), Factor, 0.3048);
	}

	// --- A unit the test cannot convert leaves the gate unrun, with a reason ---------------------------
	{
		const TCHAR* const Others[] = {TEXT("km"), TEXT("FT"), TEXT("ftus"), TEXT("us-ft"), TEXT("mm"), TEXT(" m")};
		for (const TCHAR* Other : Others)
		{
			double Factor = 42.0;
			FString Reason;
			TestFalse(*FString::Printf(TEXT("\"%s\" does not convert"), Other),
			    FMantlePlaceLiveGateLogic::TryUnitToMetres(Other, Factor, Reason));
			TestTrue(*FString::Printf(TEXT("\"%s\" is named in the reason"), Other), Reason.Contains(Other));
		}
	}

	// --- Statistics -----------------------------------------------------------------------------
	{
		FMantlePlaceDzStatistics Empty;
		TestEqual(TEXT("an empty set has mean 0"), Empty.Mean(), 0.0);
		TestEqual(TEXT("an empty set has RMS 0"), Empty.Rms(), 0.0);
		TestFalse(TEXT("an empty set passes no mean check"), FMantlePlaceLiveGateLogic::MeanWithinTolerance(Empty));
		TestFalse(TEXT("an empty set passes no RMS check"), FMantlePlaceLiveGateLogic::RmsWithinTolerance(Empty));

		FMantlePlaceDzStatistics Stats;
		Stats.Add(30.0);
		Stats.Add(-40.0);
		TestEqual(TEXT("count"), Stats.Count, 2);
		TestEqual(TEXT("mean"), Stats.Mean(), -5.0);
		TestTrue(TEXT("RMS of 30 and -40 is sqrt(1250)"), FMath::IsNearlyEqual(Stats.Rms(), FMath::Sqrt(1250.0), 1e-9));
		TestEqual(TEXT("max |dz| keeps the larger magnitude, whatever its sign"), Stats.MaxAbs, 40.0);
	}

	// --- The two thresholds ---------------------------------------------------------------------
	{
		// Symmetric noise that no placement fault explains: two points 4 m off either way, the rest near
		// the ground. A per-point bound fails it; here it must pass, since the point set is decimated.
		FMantlePlaceDzStatistics Noisy;
		for (int32 Index = 0; Index < 62; ++Index) // 64 points in all, as the live gate takes
		{
			Noisy.Add(Index % 2 == 0 ? 20.0 : -20.0);
		}
		Noisy.Add(400.0);
		Noisy.Add(-400.0);
		TestTrue(TEXT("a 4 m outlier with no offset keeps the mean in tolerance"),
		    FMantlePlaceLiveGateLogic::MeanWithinTolerance(Noisy));
		TestTrue(TEXT("... and the RMS"), FMantlePlaceLiveGateLogic::RmsWithinTolerance(Noisy));
		TestEqual(TEXT("... while the max is the outlier, logged and not judged"), Noisy.MaxAbs, 400.0);

		// A landscape 1.2 m too low: every point off. The offset fails; so does the spread.
		FMantlePlaceDzStatistics Low;
		for (int32 Index = 0; Index < 10; ++Index)
		{
			Low.Add(-120.0);
		}
		TestFalse(TEXT("a landscape 1.2 m low fails the offset check"), FMantlePlaceLiveGateLogic::MeanWithinTolerance(Low));
		TestFalse(TEXT("... and the spread check"), FMantlePlaceLiveGateLogic::RmsWithinTolerance(Low));

		// A small constant offset over the mean limit, within the RMS limit: only the mean catches it.
		FMantlePlaceDzStatistics Offset;
		for (int32 Index = 0; Index < 10; ++Index)
		{
			Offset.Add(FMantlePlaceLiveGateLogic::MaxMeanOffsetCm + 1.0);
		}
		TestFalse(TEXT("an 11 cm systematic offset fails the offset check"),
		    FMantlePlaceLiveGateLogic::MeanWithinTolerance(Offset));
		TestTrue(TEXT("... though its spread is small"), FMantlePlaceLiveGateLogic::RmsWithinTolerance(Offset));

		// Zero-mean spread over the RMS limit: only the RMS catches it.
		FMantlePlaceDzStatistics Spread;
		for (int32 Index = 0; Index < 10; ++Index)
		{
			Spread.Add(Index % 2 == 0 ? 150.0 : -150.0);
		}
		TestTrue(TEXT("a zero-mean 1.5 m scatter passes the offset check"),
		    FMantlePlaceLiveGateLogic::MeanWithinTolerance(Spread));
		TestFalse(TEXT("... and fails the spread check"), FMantlePlaceLiveGateLogic::RmsWithinTolerance(Spread));

		// The limits are inclusive and are the documented ones.
		FMantlePlaceDzStatistics AtMean;
		AtMean.Add(FMantlePlaceLiveGateLogic::MaxMeanOffsetCm);
		TestTrue(TEXT("exactly the mean limit passes"), FMantlePlaceLiveGateLogic::MeanWithinTolerance(AtMean));
		TestEqual(TEXT("mean limit is 0.10 m"), FMantlePlaceLiveGateLogic::MaxMeanOffsetCm, 10.0);
		TestEqual(TEXT("RMS limit is 1.0 m"), FMantlePlaceLiveGateLogic::MaxRmsCm, 100.0);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
