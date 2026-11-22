// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDrape.h"

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

	TestEqual(
		TEXT("the property is still the one this plugin was verified against"),
		FString(MantlePlaceDrape::LandscapeMaterialPropertyName),
		FString(TEXT("LandscapeMaterial")));

	return true;
}

#endif
