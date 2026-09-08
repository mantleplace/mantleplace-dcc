// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceScopedRestore.h"

// The defect this guard was written for: `ImportVaultPackage` overrode an editor-wide setting at the
// top and undid it before its return, so an early return added later leaked the override into the
// rest of the editor session. The property that makes that unrepeatable is "the restore runs on
// whichever path leaves the scope", and the early-return case is the one that was broken -- so it is
// the one asserted first, by a helper that actually returns early.
//
// Nothing here needs an editor, a bundle or a world, which is the point: the guard is a pure scope
// object, so the assertion runs wherever the automation suite runs.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceScopedRestoreTest,
    "MantlePlace.Import.ScopedRestore",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	/**
	 * A function shaped like the one that leaked: it changes state, guards the restore, and then
	 * returns from the middle. `bTakeEarlyReturn` picks which exit path is taken; the restore must
	 * have run either way by the time this returns.
	 */
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
			// The path the original code forgot. There is nothing to write here: the guard is the
			// whole point.
			return;
		}

		// ... the long tail of work the real function does ...
	}
}

bool FMantlePlaceScopedRestoreTest::RunTest(const FString& Parameters)
{
	// --- The early return restores, which is the defect ---------------------------------------
	{
		bool bOverrideActive = false;
		int32 RestoreCount = 0;
		ChangeStateAndReturn(/*bTakeEarlyReturn*/ true, bOverrideActive, RestoreCount);
		TestFalse(TEXT("an early return leaves the override restored"), bOverrideActive);
		TestEqual(TEXT("an early return restores exactly once"), RestoreCount, 1);
	}

	// --- So does running to the end ------------------------------------------------------------
	{
		bool bOverrideActive = false;
		int32 RestoreCount = 0;
		ChangeStateAndReturn(/*bTakeEarlyReturn*/ false, bOverrideActive, RestoreCount);
		TestFalse(TEXT("the full path leaves the override restored"), bOverrideActive);
		TestEqual(TEXT("the full path restores exactly once"), RestoreCount, 1);
	}

	// --- The restore does not run before the scope ends ----------------------------------------
	// A guard that restored eagerly would be worse than the manual pairing it replaces: the
	// suppression has to hold for the whole body.
	{
		int32 RestoreCount = 0;
		{
			const FMantlePlaceScopedRestore Guard([&RestoreCount] { ++RestoreCount; });
			TestEqual(TEXT("nothing is restored while the scope is open"), RestoreCount, 0);
			TestTrue(TEXT("an untouched guard is armed"), Guard.IsArmed());
		}
		TestEqual(TEXT("closing the scope restores"), RestoreCount, 1);
	}

	// --- Disarm abandons the restore ------------------------------------------------------------
	// For the case where the changed state is handed to something that outlives the scope.
	{
		int32 RestoreCount = 0;
		{
			FMantlePlaceScopedRestore Guard([&RestoreCount] { ++RestoreCount; });
			Guard.Disarm();
			TestFalse(TEXT("a disarmed guard is not armed"), Guard.IsArmed());
		}
		TestEqual(TEXT("a disarmed guard restores nothing"), RestoreCount, 0);
	}

	// --- Nested guards restore in reverse order -------------------------------------------------
	// Two overrides in one scope must come off in the order that leaves the editor as it was found,
	// which is the destructor order the language already gives -- asserted so a future rewrite into
	// something cleverer cannot quietly lose it.
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

	// --- An empty restore is harmless -----------------------------------------------------------
	// The importer builds its restore from a pointer it checked; a guard over nothing must not be a
	// crash on the way out of the scope.
	{
		const FMantlePlaceScopedRestore Guard{ TFunction<void()>() };
		TestFalse(TEXT("a guard over nothing is not armed"), Guard.IsArmed());
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
