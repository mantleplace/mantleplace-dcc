// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceDataLayers.h"
#include "MantlePlaceHandover.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceOdblLogic.h"

#include "Components/TextRenderComponent.h"
#include "DataLayer/DataLayerEditorSubsystem.h"
#include "Editor.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/TextRenderActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "WorldPartition/DataLayer/DataLayerAsset.h"
#include "WorldPartition/DataLayer/DataLayerInstance.h"

// ODbL content in a labelled container, and Prepare for handover, driven through the import
// context's own Claim (the one call every imported actor goes through) and the command's own entry
// point, in a throwaway world, with no bundle.
//
//  - MantlePlace.Import.Odbl.Handover: an actor imported from `database` files (a missing role
//    included) is tagged and filed under the order's ODbL outliner folder; a Produced Work is not.
//    The command removes the ODbL actors, keeps the Produced Work that stands in for a removed
//    landscape, leaves other actors alone, adds one credit actor per order that owes one, and a
//    second run changes nothing.
//  - MantlePlace.Import.Odbl.DataLayer: in a World Partition level the same actors also join the one
//    shared ODbL Data Layer, beside their family's.

namespace MantlePlaceHandoverTest
{
FMantlePlaceFileLicensing Entry(EMantlePlaceOdblRole Role, const TCHAR* SourceLicense = nullptr)
{
	FMantlePlaceFileLicensing Result;
	Result.Role = Role;
	if (SourceLicense != nullptr)
	{
		FMantlePlaceFileSource Source;
		Source.Name = TEXT("OpenStreetMap");
		Source.License = SourceLicense;
		Result.Sources.Add(Source);
	}
	return Result;
}

/** A manifest that states roles the way a 1.14.0 bundle does, and the credit it words. */
void StateRoles(FMantlePlaceVaultManifest& Manifest)
{
	Manifest.FileLicensing.Add(TEXT("Elevation/Heightmap.png"), Entry(EMantlePlaceOdblRole::Database, TEXT("ODbL-1.0")));
	Manifest.FileLicensing.Add(TEXT("Mesh/Terrain.glb"), Entry(EMantlePlaceOdblRole::ProducedWork));
	Manifest.FileLicensing.Add(TEXT("Mesh/Buildings.glb"), Entry(EMantlePlaceOdblRole::ProducedWork, TEXT("ODbL-1.0")));
	// Vector/RoadSplines.gpkg is deliberately not listed: a missing role reads as database.
	FMantlePlaceAttributionSource Osm;
	Osm.ProviderId = TEXT("openstreetmap");
	Osm.AttributionText = TEXT("(c) OpenStreetMap contributors");
	Osm.License = TEXT("ODbL-1.0");
	Manifest.Attribution.Add(Osm);
}

/** Claim one stand-in actor as a handler of this family, made from these files, would. */
AStaticMeshActor* ClaimActor(
	UWorld* World, FMantlePlaceImportContext& Context, const TCHAR* Label, EMantlePlaceLayerFamily Family,
	const TArray<FString>& Paths, const TCHAR* Group = nullptr)
{
	AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>();
	Context.Family = Family;
	Context.SourcePaths = Paths;
	Context.EquivalenceGroup = Group != nullptr ? FName(Group) : FName();
	Context.Claim(Actor, Label);
	return Actor;
}

int32 CountOf(UWorld* World, const FName& Tag)
{
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += It->Tags.Contains(Tag) ? 1 : 0;
	}
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceHandoverTest,
	"MantlePlace.Import.Odbl.Handover",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceHandoverTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceHandoverTest;
	using Family = EMantlePlaceLayerFamily;

	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: Prepare for handover needs an editor world. Skipped."));
		return true;
	}
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceHandoverTestWorld")));
	if (!TestNotNull(TEXT("a throwaway world to spawn into"), World))
	{
		return false;
	}

	FMantlePlaceVaultManifest Manifest;
	StateRoles(Manifest);
	FMantlePlaceImportSelection Selection;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	FMantlePlaceImportContext OrderA(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	OrderA.Identity = TEXT("aaaaaaaa-1111");
	FMantlePlaceImportContext OrderB(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	OrderB.Identity = TEXT("bbbbbbbb-2222");

	// Order A: a database landscape and a produced-work mesh of the same ground, a database road layer
	// with no stated role, produced-work buildings from an ODbL source. Order B: one road layer.
	AStaticMeshActor* Landscape = ClaimActor(World, OrderA, TEXT("MP_TestLandscape"), Family::Terrain,
		{ TEXT("Elevation/Heightmap.png") }, TEXT("terrain"));
	AStaticMeshActor* Mesh = ClaimActor(World, OrderA, TEXT("MP_TestMesh"), Family::Terrain,
		{ TEXT("Mesh/Terrain.glb") }, TEXT("terrain"));
	AStaticMeshActor* Road = ClaimActor(World, OrderA, TEXT("MP_TestRoad"), Family::Roads,
		{ TEXT("Vector/RoadSplines.gpkg") });
	AStaticMeshActor* Buildings = ClaimActor(World, OrderA, TEXT("MP_TestBuildings"), Family::Buildings,
		{ TEXT("Mesh/Buildings.glb") });
	AStaticMeshActor* OtherRoad = ClaimActor(World, OrderB, TEXT("MP_TestOtherRoad"), Family::Roads,
		{ TEXT("Vector/RoadSplines.gpkg") });
	AStaticMeshActor* Plain = World->SpawnActor<AStaticMeshActor>();
	Plain->SetActorLabel(TEXT("NotAnImport"));
	Plain->SetFolderPath(FName(TEXT("Mine")));

	const FName OdblTag = MantlePlaceImportNaming::OdblTag();
	const FName OdblFolderA(*MantlePlaceImportNaming::OdblFolder(OrderA.Identity));
	const FName FolderA(*MantlePlaceImportNaming::OutlinerFolder(OrderA.Identity));

	// --- Everything imported from database files lands in the labelled folder --------------------------
	TestTrue(TEXT("a database landscape is tagged ODbL"), Landscape->Tags.Contains(OdblTag));
	TestEqual(TEXT("and filed under the order's ODbL folder"), Landscape->GetFolderPath(), OdblFolderA);
	TestTrue(TEXT("a file with no stated role reads as database: tagged"), Road->Tags.Contains(OdblTag));
	TestEqual(TEXT("and filed under the ODbL folder"), Road->GetFolderPath(), OdblFolderA);
	TestEqual(TEXT("another order's road is under that order's ODbL folder"),
		OtherRoad->GetFolderPath(), FName(*MantlePlaceImportNaming::OdblFolder(OrderB.Identity)));
	TestFalse(TEXT("a Produced Work is not tagged ODbL"), Mesh->Tags.Contains(OdblTag));
	TestEqual(TEXT("and stays in the order's own folder"), Mesh->GetFolderPath(), FolderA);
	TestFalse(TEXT("Produced Work buildings are not ODbL either"), Buildings->Tags.Contains(OdblTag));
	TestEqual(TEXT("and they stay in the order's own folder"), Buildings->GetFolderPath(), FolderA);
	TestTrue(TEXT("every claimed actor still carries its import tag"),
		Road->Tags.Contains(FName(*MantlePlaceImportNaming::ImportTag(OrderA.Identity)))
		&& Mesh->Tags.Contains(FName(*MantlePlaceImportNaming::ImportTag(OrderA.Identity))));
	TestTrue(TEXT("the buildings from an ODbL source carry their credit line"),
		Buildings->Tags.Contains(FName(*MantlePlaceImportNaming::CreditLineTag(TEXT("(c) OpenStreetMap contributors")
			+ FString::Printf(TEXT(" %c "), static_cast<TCHAR>(0x2014)) + TEXT("ODbL-1.0")))));
	TestFalse(TEXT("the terrain mesh, from no ODbL source, owes none"),
		Mesh->Tags.ContainsByPredicate([](const FName& Tag) { return Tag.ToString().StartsWith(MantlePlaceImportNaming::CreditLineTagPrefix()); }));

	// --- Prepare for handover ------------------------------------------------------------------------
	const FMantlePlaceHandoverReport Report = MantlePlaceHandover::Prepare(World);
	TestEqual(TEXT("three ODbL actors removed: the landscape and both roads"), Report.Removed.Num(), 3);
	TestEqual(TEXT("one of them swapped for the Produced Work mesh"), Report.Swapped.Num(), 1);
	TestEqual(TEXT("the swapped one is the landscape"), Report.Swapped.Num() == 1 ? Report.Swapped[0] : FString(),
		FString(TEXT("MP_TestLandscape")));
	TestEqual(TEXT("no ODbL actor is left in the level"), CountOf(World, OdblTag), 0);
	TestTrue(TEXT("the Produced Work mesh stays"), IsValid(Mesh));
	TestTrue(TEXT("so do the buildings"), IsValid(Buildings));
	TestTrue(TEXT("and an actor that is not an import's"), IsValid(Plain) && Plain->GetFolderPath() == FName(TEXT("Mine")));
	TestFalse(TEXT("the removed landscape is gone"), IsValid(Landscape));
	TestFalse(TEXT("so is the road"), IsValid(Road));
	TestFalse(TEXT("so is the other order's"), IsValid(OtherRoad));

	// --- The credit -----------------------------------------------------------------------------------
	TestEqual(TEXT("a credit actor for each order that owes one"), Report.CreditActors.Num(), 2);
	int32 CreditActors = 0;
	for (TActorIterator<ATextRenderActor> It(World); It; ++It)
	{
		++CreditActors;
		const FString Text = It->GetTextRender() != nullptr ? It->GetTextRender()->Text.ToString() : FString();
		TestTrue(TEXT("the credit says what the manifest said"), Text.Contains(TEXT("(c) OpenStreetMap contributors")));
		TestTrue(TEXT("the credit actor is not an import's, so no re-run removes it"),
			!It->Tags.ContainsByPredicate([](const FName& Tag) { return Tag.ToString().StartsWith(MantlePlaceImportNaming::ImportTagPrefix()); }));
	}
	TestEqual(TEXT("two credit actors in the level"), CreditActors, 2);

	// --- A second run changes nothing ---------------------------------------------------------------------
	const FMantlePlaceHandoverReport Again = MantlePlaceHandover::Prepare(World);
	TestEqual(TEXT("a second run removes nothing"), Again.Removed.Num(), 0);
	CreditActors = 0;
	for (TActorIterator<ATextRenderActor> It(World); It; ++It)
	{
		++CreditActors;
	}
	TestEqual(TEXT("and does not duplicate the credit"), CreditActors, 2);

	// --- A level with no import ----------------------------------------------------------------------------
	UWorld* Empty = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceHandoverEmptyWorld")));
	if (TestNotNull(TEXT("a second throwaway world"), Empty))
	{
		const FMantlePlaceHandoverReport None = MantlePlaceHandover::Prepare(Empty);
		TestEqual(TEXT("nothing removed from a level with no import"), None.Removed.Num(), 0);
		TestEqual(TEXT("and no credit made up"), None.CreditActors.Num(), 0);
		TestFalse(TEXT("it says so"), None.Notes.IsEmpty());
		Empty->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	}
	TestFalse(TEXT("no level at all says so"), MantlePlaceHandover::Prepare(nullptr).Notes.IsEmpty());

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOdblDataLayerTest,
	"MantlePlace.Import.Odbl.DataLayer",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOdblDataLayerTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceHandoverTest;
	using Family = EMantlePlaceLayerFamily;

	if (GEditor == nullptr || UDataLayerEditorSubsystem::Get() == nullptr)
	{
		AddInfo(TEXT("No editor: the ODbL Data Layer needs an editor world. Skipped."));
		return true;
	}
	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceOdblDataLayerTestWorld")), /*InWorldPackage*/ nullptr, /*bAddToRoot*/ true,
		ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a throwaway partitioned world"), World))
	{
		return false;
	}

	// Under /Temp, so the shared layer assets this makes are never offered for saving.
	const FString ContentRoot = TEXT("/Temp/MantlePlaceOdblLayerTest");
	FMantlePlaceVaultManifest Manifest;
	StateRoles(Manifest);
	FMantlePlaceImportSelection Selection;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	FMantlePlaceImportResult Result;
	TArray<FString> Log;
	FMantlePlaceImportContext Order(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Order.Identity = TEXT("cccccccc-3333");
	Order.ContentRoot = ContentRoot;

	TestNull(TEXT("no ODbL layer before any ODbL content"), MantlePlaceDataLayers::FindOdbl(World, ContentRoot));
	AStaticMeshActor* Produced = ClaimActor(World, Order, TEXT("MP_TestProduced"), Family::Buildings, { TEXT("Mesh/Buildings.glb") });
	TestNull(TEXT("a Produced Work makes none"), MantlePlaceDataLayers::FindOdbl(World, ContentRoot));
	AStaticMeshActor* Road = ClaimActor(World, Order, TEXT("MP_TestRoad"), Family::Roads, { TEXT("Vector/RoadSplines.gpkg") });
	AStaticMeshActor* Landscape = ClaimActor(World, Order, TEXT("MP_TestLandscape"), Family::Terrain,
		{ TEXT("Elevation/Heightmap.png") }, TEXT("terrain"));

	UDataLayerInstance* Odbl = MantlePlaceDataLayers::FindOdbl(World, ContentRoot);
	UDataLayerInstance* Roads = MantlePlaceDataLayers::Find(World, ContentRoot, Family::Roads);
	UDataLayerInstance* Buildings = MantlePlaceDataLayers::Find(World, ContentRoot, Family::Buildings);
	if (TestNotNull(TEXT("database content makes the ODbL layer"), Odbl) && TestNotNull(TEXT("and its family's"), Roads))
	{
		TestTrue(TEXT("the road joins the ODbL layer"), Road->ContainsDataLayer(Odbl));
		TestTrue(TEXT("and still its family's"), Road->ContainsDataLayer(Roads));
		TestTrue(TEXT("the database landscape joins the one shared ODbL layer"), Landscape->ContainsDataLayer(Odbl));
		TestFalse(TEXT("the Produced Work does not"), Produced->ContainsDataLayer(Odbl));
		TestTrue(TEXT("it is only in its family's"), Buildings != nullptr && Produced->ContainsDataLayer(Buildings));
		TestTrue(TEXT("the ODbL layer is a Runtime layer a game can switch off whole"), Odbl->IsRuntime());
	}

	const FMantlePlaceHandoverReport Report = MantlePlaceHandover::Prepare(World);
	TestEqual(TEXT("Prepare for handover removes both database actors"), Report.Removed.Num(), 2);
	TestTrue(TEXT("and keeps the Produced Work"), IsValid(Produced));

	if (UDataLayerAsset* Asset = MantlePlaceDataLayers::FindAsset(ContentRoot, Family::Roads))
	{
		Asset->GetPackage()->SetDirtyFlag(false);
		Asset->ClearFlags(RF_Public | RF_Standalone);
	}
	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
