// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceTestMount.h"

#include "Engine/Level.h"
#include "HAL/FileManager.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"

// The test mount the World Partition tests save their levels under. What it pins: every package a
// saved partitioned level makes (the map, an asset beside it, an external actor, an external object)
// is under the plugin's own /MantlePlace/ root, which the plugin's asset domain covers, and resolves
// to a file in Intermediate, never in the plugin's Content.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceTestMountTest,
    "MantlePlace.Import.WorldPartition.TestMount",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTestMountTest::RunTest(const FString& Parameters)
{
	FMantlePlaceTestMount Mount(TEXT("MantlePlaceTestMountTest"));
	Mount.Mount();

	const FString Map = Mount.Root + TEXT("TestMap");
	const FString ExternalActors = ULevel::GetExternalActorsPath(Map);
	const FString ExternalObjects = ExternalActors.Replace(TEXT("/__ExternalActors__/"), TEXT("/__ExternalObjects__/"));
	const TArray<FString> Packages = {
		Map,
		Mount.Root + TEXT("Content/Layers/DL_Terrain"),
		ExternalActors / TEXT("0/AB/CDEFGHIJKLMNOPQRSTUVWX"),
		ExternalObjects / TEXT("0/AB/CDEFGHIJKLMNOPQRSTUVWX"),
	};
	const FString PluginContent = FPaths::ConvertRelativePathToFull(
	    FPackageName::LongPackageNameToFilename(TEXT("/MantlePlace/")));
	for (const FString& Package : Packages)
	{
		const FString File = FPaths::ConvertRelativePathToFull(
		    FPackageName::LongPackageNameToFilename(Package, FPackageName::GetAssetPackageExtension()));
		TestTrue(*FString::Printf(TEXT("%s is under the plugin's root"), *Package), Package.StartsWith(TEXT("/MantlePlace/")));
		TestTrue(*FString::Printf(TEXT("%s is one of the mount's packages"), *Package), Mount.Owns(Package));
		TestTrue(*FString::Printf(TEXT("%s saves to Intermediate (got %s)"), *Package, *File), File.StartsWith(Mount.DiskRoot));
		TestFalse(*FString::Printf(TEXT("%s does not save to the plugin's Content"), *Package), File.StartsWith(PluginContent));
	}

	Mount.Unmount();
	TestFalse(TEXT("unmounted: the folder is gone"), IFileManager::Get().DirectoryExists(*Mount.DiskRoot));
	TestTrue(TEXT("unmounted: the plugin's root resolves to its Content again"),
	         FPaths::ConvertRelativePathToFull(FPackageName::LongPackageNameToFilename(Map)).StartsWith(PluginContent));
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
