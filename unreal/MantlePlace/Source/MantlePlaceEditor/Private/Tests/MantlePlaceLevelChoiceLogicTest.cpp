// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceLevelChoiceLogic.h"
#include "MantlePlaceManifestThreeLogic.h"
#include "MantlePlaceVaultTypes.h"

namespace
{
FMantlePlaceLevelSize Size(const TCHAR* Level, int64 Bytes, int64 Rows, int64 Features, int64 Triangles, int64 Pixels)
{
	FMantlePlaceLevelSize Out;
	Out.Level = Level;
	Out.SizeBytes = Bytes;
	Out.Rows = Rows;
	Out.Features = Features;
	Out.Triangles = Triangles;
	Out.Pixels = Pixels;
	return Out;
}

FMantlePlaceVaultItem ThreeOrder(const TCHAR* Recommended)
{
	FMantlePlaceVaultItem Item;
	Item.OrderId = TEXT("order-3");
	Item.bHasLevels = true;
	Item.RecommendedLevel = Recommended;
	Item.Levels = {
		Size(TEXT("Max"), 4000000000, 800, 120, 4000000, 16000000),
		Size(TEXT("Med"), 1400000000, 400, 120, 2000000, 4000000),
		Size(TEXT("Min"), 300000000, 200, 120, 1000000, 1000000),
	};
	return Item;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLevelChoiceStateTest,
	"MantlePlace.Vault.LevelChoice.State",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLevelChoiceStateTest::RunTest(const FString& Parameters)
{
	using FChoice = FMantlePlaceLevelChoice;

	TestEqual(TEXT("The heading is the standard's"), FChoice::Heading(), FString(TEXT("Fidelity Level")));
	TestEqual(TEXT("The button is the standard's"), FChoice::DownloadWord(), FString(TEXT("Download")));

	FMantlePlaceVaultItem TwoX;
	TwoX.OrderId = TEXT("order-2");
	TestFalse(TEXT("A 2.x order offers no level control"), FChoice::For(TwoX).IsValid());

	const TSharedPtr<FChoice> Choice = FChoice::For(ThreeOrder(TEXT("Med")));
	if (!TestTrue(TEXT("A 3.0 order offers the control"), Choice.IsValid()))
	{
		return false;
	}
	TestEqual(TEXT("It is for the order"), Choice->OrderId(), FString(TEXT("order-3")));

	const TArray<FMantlePlaceLevelOption>& Options = Choice->Options();
	if (TestEqual(TEXT("Three levels"), Options.Num(), 3))
	{
		TestEqual(TEXT("Max, with its size"), Options[0].Label, FString(TEXT("Max — 3.7 GB")));
		TestEqual(TEXT("Med, with its size and the recommendation"), Options[1].Label, FString(TEXT("Med — 1.3 GB (Recommended)")));
		TestEqual(TEXT("Min, with its size"), Options[2].Label, FString(TEXT("Min — 286.1 MB")));
		TestTrue(TEXT("Only Med is recommended"), !Options[0].bRecommended && Options[1].bRecommended && !Options[2].bRecommended);
		TestEqual(TEXT("The counts are the tooltip"), Options[1].ToolTip,
			FString(TEXT("Med: 400 rows, 120 features, 2,000,000 triangles, 4,000,000 pixels")));
	}

	TestEqual(TEXT("The recommendation is preselected"), Choice->Selected(), FString(TEXT("Med")));
	TestTrue(TEXT("So Download can go"), Choice->HasSelection());
	TestTrue(TEXT("The curator chooses Max"), Choice->Choose(TEXT("Max")));
	TestEqual(TEXT("Max is selected"), Choice->Selected(), FString(TEXT("Max")));
	TestFalse(TEXT("A level not offered is refused"), Choice->Choose(TEXT("RAW")));
	TestFalse(TEXT("A 2.x token is not a level here"), Choice->Choose(TEXT("MIN")));
	TestEqual(TEXT("And changes nothing"), Choice->Selected(), FString(TEXT("Max")));

	int32 Calls = 0;
	FString Confirmed;
	Choice->OnConfirmed = [&Calls, &Confirmed](const FString& Level) { ++Calls; Confirmed = Level; };
	TestFalse(TEXT("Not confirmed before Download"), Choice->IsConfirmed());
	TestTrue(TEXT("Download confirms"), Choice->Confirm());
	TestEqual(TEXT("With the chosen level"), Confirmed, FString(TEXT("Max")));
	TestTrue(TEXT("Confirmed"), Choice->IsConfirmed());
	TestFalse(TEXT("A second press does nothing"), Choice->Confirm());
	TestEqual(TEXT("The download is asked for once"), Calls, 1);
	TestFalse(TEXT("The level is fixed once downloading"), Choice->Choose(TEXT("Min")));
	TestEqual(TEXT("Still Max"), Choice->Selected(), FString(TEXT("Max")));

	TestEqual(TEXT("A new control starts on the recommendation"), FChoice::For(ThreeOrder(TEXT("Med")))->Selected(), FString(TEXT("Med")));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLevelChoiceNoneRecommendedTest,
	"MantlePlace.Vault.LevelChoice.NoneRecommended",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLevelChoiceNoneRecommendedTest::RunTest(const FString& Parameters)
{
	const TSharedPtr<FMantlePlaceLevelChoice> Choice = FMantlePlaceLevelChoice::For(ThreeOrder(TEXT("")));
	if (!TestTrue(TEXT("The levels are still offered"), Choice.IsValid()))
	{
		return false;
	}
	TestFalse(TEXT("Nothing is preselected"), Choice->HasSelection());
	TestTrue(TEXT("No option says Recommended"), !Choice->Options().ContainsByPredicate(
		[](const FMantlePlaceLevelOption& Option) { return Option.bRecommended || Option.Label.Contains(TEXT("Recommended")); }));
	TestEqual(TEXT("And the control says so"), Choice->Note(), FMantlePlaceLevelChoice::NoneRecommendedNote());
	int32 Calls = 0;
	Choice->OnConfirmed = [&Calls](const FString&) { ++Calls; };
	TestFalse(TEXT("Download needs a choice"), Choice->Confirm());
	TestEqual(TEXT("Nothing was asked for"), Calls, 0);
	TestTrue(TEXT("Once chosen"), Choice->Choose(TEXT("Min")));
	TestTrue(TEXT("Download goes"), Choice->Confirm());
	TestEqual(TEXT("Once"), Calls, 1);
	TestTrue(TEXT("With a recommendation there is no note"), FMantlePlaceLevelChoice::For(ThreeOrder(TEXT("Max")))->Note().IsEmpty());
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLevelChoiceFilesTest,
	"MantlePlace.Vault.LevelChoice.Files",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLevelChoiceFilesTest::RunTest(const FString& Parameters)
{
	using F3 = FMantlePlaceManifestThreeLogic;
	const TArray<FString>& Files = F3::RequestedFiles();
	for (const TCHAR* Step : { F3::HeightmapPath(), F3::DrapePath(), F3::TerrainMeshPath(), F3::BuildingsPath(), F3::TreesPath(), F3::RoadsPath() })
	{
		TestTrue(FString::Printf(TEXT("The request names %s"), Step), Files.Contains(Step));
	}
	for (const TCHAR* Map : { TEXT("Water"), TEXT("Grass"), TEXT("Forest"), TEXT("Dirt"), TEXT("Rock"), TEXT("Sand"), TEXT("Snow"), TEXT("Built") })
	{
		TestTrue(FString::Printf(TEXT("The request names the %s weight map"), Map),
			Files.Contains(FString::Printf(TEXT("Materials/%s.png"), Map)));
	}
	for (const TCHAR* Record : { TEXT("Parcels"), TEXT("Zoning"), TEXT("Floodplain"), TEXT("Wetlands"), TEXT("Soils"),
			 TEXT("Heritage"), TEXT("Conservation"), TEXT("Hydrography"), TEXT("Contamination") })
	{
		TestTrue(FString::Printf(TEXT("The request names the %s linework"), Record),
			Files.Contains(FString::Printf(TEXT("Records/%s.dxf"), Record)));
	}
	TestFalse(TEXT("Habitat is not in the published description, so it is not asked for"),
		Files.ContainsByPredicate([](const FString& File) { return File.Contains(TEXT("Habitat")); }));
	TestEqual(TEXT("Six step files, eight weight maps and nine record files"), Files.Num(), 23);
	TestFalse(TEXT("No record is asked for as anything but linework"), Files.ContainsByPredicate(
		[](const FString& File) { return File.StartsWith(F3::RecordsFolder()) && !File.EndsWith(TEXT(".dxf")); }));
	TestFalse(TEXT("A georeferenced file is never asked for"), Files.ContainsByPredicate(
		[](const FString& File) { return File.EndsWith(TEXT(".tif")) || File.EndsWith(TEXT(".gpkg")); }));
	TestFalse(TEXT("Manifest.json comes anyway and is not named"), Files.Contains(TEXT("Manifest.json")));
	return true;
}

#endif
