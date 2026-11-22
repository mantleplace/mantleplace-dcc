// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceScopedRestore.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceScopedRestoreTest,
    "MantlePlace.Import.ScopedRestore",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	void ChangeStateAndReturn(bool bTakeEarlyReturn, bool& bOverrideActive, int32& RestoreCount)
	{
		bOverrideActive = true;
		const FMantlePlaceScopedRestore RestoreOverride(
			[&bOverrideActive, &RestoreCount]
			{
				bOverrideActive = false;
				++RestoreCount;
			});

		if (bTakeEarlyReturn)
		{
			return;
		}
	}
}

bool FMantlePlaceScopedRestoreTest::RunTest(const FString& Parameters)
{
	{
		bool bOverrideActive = false;
		int32 RestoreCount = 0;
		ChangeStateAndReturn(true, bOverrideActive, RestoreCount);
		TestFalse(TEXT("an early return leaves the override restored"), bOverrideActive);
		TestEqual(TEXT("an early return restores exactly once"), RestoreCount, 1);
	}

	{
		bool bOverrideActive = false;
		int32 RestoreCount = 0;
		ChangeStateAndReturn(false, bOverrideActive, RestoreCount);
		TestFalse(TEXT("the full path leaves the override restored"), bOverrideActive);
		TestEqual(TEXT("the full path restores exactly once"), RestoreCount, 1);
	}

	{
		int32 RestoreCount = 0;
		{
			const FMantlePlaceScopedRestore Guard([&RestoreCount] { ++RestoreCount; });
			TestEqual(TEXT("nothing is restored while the scope is open"), RestoreCount, 0);
			TestTrue(TEXT("an untouched guard is armed"), Guard.IsArmed());
		}
		TestEqual(TEXT("closing the scope restores"), RestoreCount, 1);
	}

	{
		int32 RestoreCount = 0;
		{
			FMantlePlaceScopedRestore Guard([&RestoreCount] { ++RestoreCount; });
			Guard.Disarm();
			TestFalse(TEXT("a disarmed guard is not armed"), Guard.IsArmed());
		}
		TestEqual(TEXT("a disarmed guard restores nothing"), RestoreCount, 0);
	}

	{
		TArray<int32> Order;
		{
			const FMantlePlaceScopedRestore Outer([&Order] { Order.Add(1); });
			const FMantlePlaceScopedRestore Inner([&Order] { Order.Add(2); });
		}
		TestEqual(TEXT("two guards both restore"), Order.Num(), 2);
		if (Order.Num() == 2)
		{
			TestEqual(TEXT("the inner guard restores first"), Order[0], 2);
			TestEqual(TEXT("the outer guard restores second"), Order[1], 1);
		}
	}

	{
		const FMantlePlaceScopedRestore Guard{ TFunction<void()>() };
		TestFalse(TEXT("a guard over nothing is not armed"), Guard.IsArmed());
	}

	return true;
}

#endif
