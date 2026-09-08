// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDrape.h"

// The engine-version tripwire for the one place this plugin reaches into the engine by name.
//
// `MantlePlaceDrape::AssignMaterial` drives ALandscapeProxy's `LandscapeMaterial` property through
// reflection, because the setter that would do it properly — EditorSetLandscapeMaterial — is not
// exported from the Landscape module. Reflection is invisible to the compiler: an engine rename
// produces a clean build and then fails at the one moment it costs the most, in a user's editor
// session, halfway through their import.
//
// AssignMaterial now fails closed rather than crashing there, which makes the rename survivable.
// This test is the other half: it asks the same question ahead of time, so a rename is a red
// automation suite rather than a silently undraped landscape nobody connects to an engine upgrade.
// It is the assertion to read first when the drape stops appearing after an engine bump.
//
// Verified against UE 5.8. If this goes red on a newer engine, the property has moved or been
// renamed: find its new name and change MantlePlaceDrape::LandscapeMaterialPropertyName, which is
// the only place it is spelled.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceDrapeTripwireTest,
    "MantlePlace.Import.DrapeEngineTripwire",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceDrapeTripwireTest::RunTest(const FString& Parameters)
{
	TestTrue(
		TEXT("ALandscapeProxy still exposes the property AssignMaterial drives by name — if this "
			 "fails, the engine renamed it and the drape will silently stop being assigned"),
		MantlePlaceDrape::IsLandscapeMaterialPropertyResolvable());

	// The name itself is asserted literally, for the same reason the naming test asserts the strings
	// the importer writes: this one is a contract with the ENGINE rather than with a user, and an
	// edit to it is a decision about which engine builds this plugin still drapes on.
	TestEqual(
		TEXT("the property is still the one this plugin was verified against"),
		FString(MantlePlaceDrape::LandscapeMaterialPropertyName),
		FString(TEXT("LandscapeMaterial")));

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
