// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCuratorExtentLogic.h"
#include "MantlePlaceExtentBox.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceLevelExtent.h"

#include "Editor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/FileManager.h"

// Open in curator's engine reads, over a throwaway world: the Extent box actor (placed, scaled, read
// in W,S,E,N degrees), the previous-import read (the actors' import tag, then the record under
// Saved), and the two georeference reads, which reach Epic's GeoReferencing and Cesium for Unreal by
// reflection. The decisions they feed are asserted headlessly in MantlePlace.CuratorExtent.Sources
// and .Precedence.
//
// A georeference read is exercised only when its plugin is enabled in the project the test runs in
// (the class is looked up, never loaded, and a plugin that is off has no class). With it off the test
// asserts the other half of the contract: the read yields nothing, and the plugin loaded without it.

namespace
{
	/** The throwaway world; the caller destroys it. */
	UWorld* NewWorld(const TCHAR* Name)
	{
		return UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false, FName(Name));
	}

	/** One degree is about 111 km, so this is about 2 km of slack on a read that is exact to metres. */
	constexpr double GeoSlackDeg = 0.02;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceLevelExtentTest,
	"MantlePlace.CuratorExtent.LevelRead",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLevelExtentTest::RunTest(const FString& Parameters)
{
	// --- The Extent box is a placeable actor with the two location properties, editor-only ------------
	{
		const UClass* BoxClass = AMantlePlaceExtentBox::StaticClass();
		for (const TCHAR* Name : { TEXT("CenterLongitude"), TEXT("CenterLatitude") })
		{
			const FProperty* Property = BoxClass->FindPropertyByName(FName(Name));
			if (TestNotNull(FString::Printf(TEXT("the Extent box has a %s property"), Name), Property))
			{
				TestTrue(FString::Printf(TEXT("%s is editable in Details"), Name), Property->HasAnyPropertyFlags(CPF_Edit));
			}
		}
		TestFalse(TEXT("the Extent box is placeable from the editor"), BoxClass->HasAnyClassFlags(CLASS_NotPlaceable));
		TestTrue(TEXT("and is stripped from a cooked build"), GetDefault<AMantlePlaceExtentBox>()->IsEditorOnly());
		TestNotNull(TEXT("its box exists on the default object, so every spawn has one"), GetDefault<AMantlePlaceExtentBox>()->Bounds.Get());
	}

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: the level reads need an editor world. Skipped."));
		return true;
	}

	// --- The Extent box: placed, located, scaled and read ---------------------------------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceExtentBoxTestWorld"));
		if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
		{
			return false;
		}

		TestEqual(TEXT("an empty level has no Extent box"), MantlePlaceLevelExtent::ExtentBoxes(World).Num(), 0);
		AMantlePlaceExtentBox* Box = MantlePlaceLevelExtent::PlaceExtentBox(World);
		if (TestNotNull(TEXT("the Extent box is placed"), Box))
		{
			TestEqual(TEXT("it carries the generated label"), Box->GetActorLabel(), MantlePlaceImportNaming::ExtentBoxLabel());
			TestTrue(TEXT("placing again selects the box that is there"), MantlePlaceLevelExtent::PlaceExtentBox(World) == Box);
			TestEqual(TEXT("and does not stack a second"), MantlePlaceLevelExtent::ExtentBoxes(World).Num(), 1);

			FMantlePlaceExtent Extent;
			TestFalse(TEXT("a box nobody located reads as no extent"), Box->GetExtent(Extent));
			TestFalse(TEXT("so the level has no source and the plain curator opens"), MantlePlaceLevelExtent::Resolve(World).IsSet());

			// Placed at the default size: 1 km, before any scaling.
			TestEqual(TEXT("default width is 1 km"), Box->GetState().WidthM, 1000.0, 1e-6);
			TestEqual(TEXT("default depth is 1 km"), Box->GetState().DepthM, 1000.0, 1e-6);

			Box->CenterLongitude = -97.7431;
			Box->CenterLatitude = 30.2672;
			TestTrue(TEXT("located, the box reads"), Box->GetExtent(Extent));
			TestTrue(TEXT("its extent is valid W,S,E,N degrees"), Extent.IsValid());
			TestEqual(TEXT("centred on the longitude"), (Extent.West + Extent.East) * 0.5, -97.7431, 1e-9);
			TestEqual(TEXT("centred on the latitude"), (Extent.South + Extent.North) * 0.5, 30.2672, 1e-9);
			const double BaseLongitudeSpan = Extent.East - Extent.West;
			const double BaseLatitudeSpan = Extent.North - Extent.South;
			TestEqual(TEXT("1 km is about 0.0104 degrees of longitude here"), BaseLongitudeSpan, 0.0104, 0.0001);
			TestEqual(TEXT("1 km is about 0.0090 degrees of latitude"), BaseLatitudeSpan, 0.0090, 0.0001);

			// Scaled: X is east-west, Y is north-south. Rotation does not move the extent.
			Box->SetActorScale3D(FVector(2.0, 3.0, 1.0));
			Box->SetActorRotation(FRotator(0.0, 45.0, 0.0));
			TestEqual(TEXT("scaled X is the width"), Box->GetState().WidthM, 2000.0, 1e-6);
			TestEqual(TEXT("scaled Y is the depth"), Box->GetState().DepthM, 3000.0, 1e-6);
			FMantlePlaceExtent Scaled;
			TestTrue(TEXT("the scaled box reads"), Box->GetExtent(Scaled));
			TestEqual(TEXT("twice the longitude span"), Scaled.East - Scaled.West, 2.0 * BaseLongitudeSpan, 1e-9);
			TestEqual(TEXT("three times the latitude span"), Scaled.North - Scaled.South, 3.0 * BaseLatitudeSpan, 1e-9);

			const FMantlePlaceResolvedExtent Resolved = MantlePlaceLevelExtent::Resolve(World);
			TestTrue(TEXT("a located box is the level's extent source"), Resolved.Source == EMantlePlaceExtentSource::ExtentBox);
			TestEqual(TEXT("and the level resolves to the box's extent"), Resolved.Extent.West, Scaled.West, 1e-12);
		}
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	// --- A previous import: the tag finds the order, the record holds its extent ----------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceImportExtentTestWorld"));
		if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
		{
			return false;
		}

		// Two identities that share their first eight characters share a record file.
		const FString Identity = TEXT("00000519-aaaa");
		const FString Colliding = TEXT("00000519-bbbb");

		AActor* Imported = World->SpawnActor<AActor>();
		Imported->Tags.Add(FName(*MantlePlaceImportNaming::ImportTag(Identity)));
		TestEqual(TEXT("an import with no record gives no extent"), MantlePlaceLevelExtent::ImportExtents(World).Num(), 0);

		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Colliding;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		Record.bHasExtent = true;
		Record.Extent.West = -97.75;
		Record.Extent.South = 30.25;
		Record.Extent.East = -97.73;
		Record.Extent.North = 30.27;
		MantlePlaceImportProvenance::Write(Record);
		TestEqual(TEXT("another order's record, in the same file, is not this import's"),
			MantlePlaceLevelExtent::ImportExtents(World).Num(), 0);

		Record.Identity = Identity;
		MantlePlaceImportProvenance::Write(Record);
		const TArray<FMantlePlaceExtent> Extents = MantlePlaceLevelExtent::ImportExtents(World);
		if (TestEqual(TEXT("the import's own record gives its extent"), Extents.Num(), 1))
		{
			TestEqual(TEXT("west"), Extents[0].West, -97.75);
			TestEqual(TEXT("north"), Extents[0].North, 30.27);
		}

		// An older record with no extent says nothing, and the level falls through to the next source.
		Record.bHasExtent = false;
		MantlePlaceImportProvenance::Write(Record);
		TestEqual(TEXT("a record from an earlier build has no extent"), MantlePlaceLevelExtent::ImportExtents(World).Num(), 0);

		// The previous import outranks an Extent box in the same level.
		Record.bHasExtent = true;
		MantlePlaceImportProvenance::Write(Record);
		AMantlePlaceExtentBox* Box = World->SpawnActor<AMantlePlaceExtentBox>();
		Box->CenterLongitude = -90.0;
		Box->CenterLatitude = 40.0;
		const FMantlePlaceResolvedExtent Resolved = MantlePlaceLevelExtent::Resolve(World);
		TestTrue(TEXT("the previous import wins over the Extent box"), Resolved.Source == EMantlePlaceExtentSource::PriorImport);
		TestEqual(TEXT("with its own extent"), Resolved.Extent.East, -97.73);

		IFileManager::Get().Delete(*MantlePlaceImportProvenance::RecordPath(Identity), /*bRequireExists*/ false, /*bEvenReadOnly*/ true);
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	// --- The georeferences, read by reflection --------------------------------------------------------
	{
		UWorld* World = NewWorld(TEXT("MantlePlaceGeoreferenceTestWorld"));
		if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
		{
			return false;
		}
		// A kilometre square about the world origin, where both systems anchor their origin.
		const FBox Bounds(FVector(-50000.0, -50000.0, 0.0), FVector(50000.0, 50000.0, 0.0));

		TestEqual(TEXT("no georeference actor in the level: no native read"),
			MantlePlaceLevelExtent::ConvertCorners(World, Bounds, /*bNative*/ true).Num(), 0);
		TestEqual(TEXT("no georeference actor in the level: no Cesium read"),
			MantlePlaceLevelExtent::ConvertCorners(World, Bounds, /*bNative*/ false).Num(), 0);
		TestEqual(TEXT("empty bounds read nothing"),
			MantlePlaceLevelExtent::ConvertCorners(World, FBox(ForceInit), /*bNative*/ true).Num(), 0);

		// Epic's GeoReferencing: the default system is EPSG:32631 with the origin at 500000 E, 5000000 N,
		// which is 3.0 E, 45.1535 N.
		if (const UClass* NativeClass = FindObject<UClass>(nullptr, MantlePlaceLevelExtent::NativeGeoreferenceClassPath()))
		{
			AActor* System = World->SpawnActor(const_cast<UClass*>(NativeClass));
			if (TestNotNull(TEXT("the GeoReferencing system spawns"), System))
			{
				const TArray<FMantlePlaceGeoPoint> Corners = MantlePlaceLevelExtent::ConvertCorners(World, Bounds, /*bNative*/ true);
				if (TestEqual(TEXT("the native read gives the four corners"), Corners.Num(), 4))
				{
					FMantlePlaceExtent Extent;
					TestTrue(TEXT("the corners make an extent"), FMantlePlaceCuratorExtentLogic::FromNativeGeoreference(Corners, Extent));
					TestTrue(TEXT("it encloses the origin the system states"),
						Extent.West < 3.0 && Extent.East > 3.0 && Extent.South < 45.1535 && Extent.North > 45.1535);
					TestTrue(TEXT("and is about a kilometre across"),
						(Extent.East - Extent.West) > 0.005 && (Extent.East - Extent.West) < GeoSlackDeg
						&& (Extent.North - Extent.South) > 0.005 && (Extent.North - Extent.South) < GeoSlackDeg);
				}
			}
		}
		else
		{
			AddInfo(TEXT("The GeoReferencing plugin is not enabled in this project: the native read was not exercised, and the plugin loaded without it."));
		}

		// Cesium for Unreal: a default georeference puts the Unreal origin at -105.25737 E, 39.736401 N.
		if (const UClass* CesiumClass = FindObject<UClass>(nullptr, MantlePlaceLevelExtent::CesiumGeoreferenceClassPath()))
		{
			AActor* Georeference = World->SpawnActor(const_cast<UClass*>(CesiumClass));
			if (TestNotNull(TEXT("the Cesium georeference spawns"), Georeference))
			{
				const TArray<FMantlePlaceGeoPoint> Corners = MantlePlaceLevelExtent::ConvertCorners(World, Bounds, /*bNative*/ false);
				if (TestEqual(TEXT("the Cesium read gives the four corners"), Corners.Num(), 4))
				{
					FMantlePlaceExtent Extent;
					TestTrue(TEXT("the corners make an extent"), FMantlePlaceCuratorExtentLogic::FromCesiumGeoreference(Corners, Extent));
					TestTrue(TEXT("it encloses the origin the georeference states"),
						Extent.West < -105.25737 && Extent.East > -105.25737 && Extent.South < 39.736401 && Extent.North > 39.736401);
					TestTrue(TEXT("and is about a kilometre across"),
						(Extent.East - Extent.West) > 0.005 && (Extent.East - Extent.West) < GeoSlackDeg
						&& (Extent.North - Extent.South) > 0.005 && (Extent.North - Extent.South) < GeoSlackDeg);
				}
			}
		}
		else
		{
			AddInfo(TEXT("Cesium for Unreal is not enabled in this project: the Cesium read was not exercised, and the plugin loaded without it."));
		}
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}

	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
