// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportTiming.h"

#include "Editor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "Subsystems/EditorAssetSubsystem.h"

// The import's orchestration, with no bundle: which artifacts a Mode fans out to, the order the
// registry runs them in, that a selection of artifacts gates each step, the refusal that must come
// BEFORE the destructive wipe, the provenance-guarded asset wipe itself, and that a re-import
// REPLACES the prior actors rather than stacking them. This is the safety net under ImportVaultPackage's shrink to orchestration: the per-artifact
// work is the handlers' and is covered where it always was (the Logic units, the live-bundle test);
// what this covers is the wiring between them.

namespace
{
/** A handler that does nothing but record being called, so ORDER and GATING can be asserted. */
class FRecordingHandler final : public IMantlePlaceArtifactHandler
{
public:
	FRecordingHandler(FName InId, TArray<FString>& InCalls, bool bInTerrain = false, bool bInPresent = true)
		: IdValue(InId), Calls(InCalls), bTerrain(bInTerrain), bPresent(bInPresent)
	{
	}

	FName Id() const override { return IdValue; }
	FText DisplayName() const override { return FText::FromName(IdValue); }
	bool ParseFields(const FMantlePlaceParseInput&, FMantlePlaceVaultManifest&, FString&) const override { return true; }
	bool IsPresent(const FMantlePlaceVaultManifest&) const override { return bPresent; }
	void CollectDeclared(const FMantlePlaceVaultManifest&, TArray<FMantlePlaceDeclaredArtifact>&) const override {}
	bool IsTerrainRepresentation() const override { return bTerrain; }
	void ExplainAbsence(const FMantlePlaceVaultManifest&, TArray<FString>& Out) const override
	{
		Out.Add(FString::Printf(TEXT("no %s"), *IdValue.ToString()));
	}
	void Prepare(FMantlePlaceImportContext&) override { Calls.Add(IdValue.ToString() + TEXT(".Prepare")); }
	void Import(FMantlePlaceImportContext&) override { Calls.Add(IdValue.ToString() + TEXT(".Import")); }
	void Finalize(FMantlePlaceImportContext&) override { Calls.Add(IdValue.ToString() + TEXT(".Finalize")); }

private:
	FName IdValue;
	TArray<FString>& Calls;
	bool bTerrain;
	bool bPresent;
};

TArray<FString> IdsIn(const MantlePlaceArtifacts::FHandlers& Handlers, const FMantlePlaceImportSelection& Selection)
{
	TArray<FString> Ids;
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Selection.Includes(Handler->Id()))
		{
			Ids.Add(Handler->Id().ToString());
		}
	}
	return Ids;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOrchestrationFanOutTest,
	"MantlePlace.Import.Orchestration.ModeFanOut",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOrchestrationFanOutTest::RunTest(const FString& Parameters)
{
	// The registry is the explicit, ordered list: the drape first (the landscape adopts its material),
	// every artifact exactly once, no discovery. Pinning it makes a reorder a visible, reviewed change.
	const MantlePlaceArtifacts::FHandlers Registry = MantlePlaceArtifacts::MakeAll();
	TArray<FString> AllIds = IdsIn(Registry, FMantlePlaceImportSelection::Everything());
	const TArray<FString> ExpectedAll = {
		TEXT("drape"), TEXT("landscape"), TEXT("coverage_rasters"), TEXT("terrain_mesh"),
		TEXT("buildings"), TEXT("road_splines"), TEXT("foliage_points"), TEXT("public_records") };
	TestEqual(TEXT("registry: every artifact, in import order"), FString::Join(AllIds, TEXT(",")), FString::Join(ExpectedAll, TEXT(",")));

	// Mode fan-out. Landscape and Mesh are alternatives for the terrain; the rest is content that
	// imports whenever the bundle ships it, whichever Mode was asked for.
	TArray<FString> NoMesh = ExpectedAll;
	NoMesh.Remove(TEXT("terrain_mesh"));
	TArray<FString> NoLandscape = ExpectedAll;
	NoLandscape.Remove(TEXT("landscape"));

	TestEqual(TEXT("Landscape mode: everything but the terrain mesh"),
		FString::Join(IdsIn(Registry, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Landscape)), TEXT(",")),
		FString::Join(NoMesh, TEXT(",")));
	TestEqual(TEXT("Mesh mode: everything but the landscape"),
		FString::Join(IdsIn(Registry, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Mesh)), TEXT(",")),
		FString::Join(NoLandscape, TEXT(",")));
	TestEqual(TEXT("Both mode: everything"),
		FString::Join(IdsIn(Registry, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Both)), TEXT(",")),
		FString::Join(ExpectedAll, TEXT(",")));

	// Per-layer gating is DATA: any subset, and an artifact nobody named stays in by default.
	FMantlePlaceImportSelection BuildingsOff;
	BuildingsOff.Excluded.Add(FName(TEXT("buildings")));
	TestFalse(TEXT("selection: an excluded layer is out"), BuildingsOff.Includes(FName(TEXT("buildings"))));
	TestTrue(TEXT("selection: an unnamed layer is in"), BuildingsOff.Includes(FName(TEXT("road_splines"))));
	TestTrue(TEXT("selection: a layer from a future registry is in by default"), BuildingsOff.Includes(FName(TEXT("a_ninth_artifact"))));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOrchestrationRunTest,
	"MantlePlace.Import.Orchestration.RunAndRefusal",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOrchestrationRunTest::RunTest(const FString& Parameters)
{
	// --- The walk: registry order, three steps, an excluded handler is never called ------------------
	{
		TArray<FString> Calls;
		MantlePlaceArtifacts::FHandlers Handlers;
		Handlers.Add(MakeUnique<FRecordingHandler>(FName(TEXT("a")), Calls));
		Handlers.Add(MakeUnique<FRecordingHandler>(FName(TEXT("b")), Calls));
		Handlers.Add(MakeUnique<FRecordingHandler>(FName(TEXT("c")), Calls));

		FMantlePlaceImportSelection Selection;
		Selection.Excluded.Add(FName(TEXT("b")));

		FMantlePlaceVaultManifest Manifest;
		MantlePlaceImportTiming::FTimeline Timeline;
		FMantlePlaceImportResult Result;
		TArray<FString> Log;
		FMantlePlaceImportContext Context(nullptr, Manifest, nullptr, Selection, Timeline, Result, Log);

		MantlePlaceImportOrchestration::RunPrepare(Handlers, Context);
		MantlePlaceImportOrchestration::RunImport(Handlers, Context);
		MantlePlaceImportOrchestration::RunFinalize(Handlers, Context);

		TestEqual(TEXT("walk: Prepare, then Import, then Finalize, in registry order, skipping the excluded"),
			FString::Join(Calls, TEXT(" ")),
			TEXT("a.Prepare c.Prepare a.Import c.Import a.Finalize c.Finalize"));
	}

	// --- Nothing to import: decided from the selection, before the wipe -----------------------------
	{
		TArray<FString> Calls;
		const auto Build = [&Calls](bool bLandscapePresent, bool bMeshPresent)
		{
			MantlePlaceArtifacts::FHandlers Handlers;
			Handlers.Add(MakeUnique<FRecordingHandler>(MantlePlaceArtifactId::Landscape(), Calls, true, bLandscapePresent));
			Handlers.Add(MakeUnique<FRecordingHandler>(MantlePlaceArtifactId::TerrainMesh(), Calls, true, bMeshPresent));
			Handlers.Add(MakeUnique<FRecordingHandler>(FName(TEXT("buildings")), Calls, false, false));
			return Handlers;
		};
		FMantlePlaceVaultManifest Manifest;
		const FMantlePlaceImportSelection Both = FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Both);

		TestTrue(TEXT("a mesh-only bundle imports in Both mode"),
			MantlePlaceImportOrchestration::ExplainNothingToImport(Build(false, true), Manifest, Both).IsEmpty());

		const FString Refusal = MantlePlaceImportOrchestration::ExplainNothingToImport(
			Build(false, true), Manifest, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Landscape));
		TestTrue(TEXT("a Landscape request on a mesh-only bundle is refused"), Refusal.StartsWith(TEXT("Nothing to import: ")));
		TestTrue(TEXT("the refusal names what is missing"), Refusal.Contains(TEXT("no landscape")));
		TestTrue(TEXT("the refusal promises the existing assets are untouched"), Refusal.Contains(TEXT("Existing assets left untouched")));
		TestFalse(TEXT("the refusal does not blame the mesh the request never asked for"), Refusal.Contains(TEXT("no terrain_mesh")));

		TestFalse(TEXT("both representations absent is refused"),
			MantlePlaceImportOrchestration::ExplainNothingToImport(Build(false, false), Manifest, Both).IsEmpty());

		FMantlePlaceImportSelection NoTerrain;
		NoTerrain.Excluded.Add(MantlePlaceArtifactId::Landscape());
		NoTerrain.Excluded.Add(MantlePlaceArtifactId::TerrainMesh());
		TestTrue(TEXT("a selection that asks for no terrain asks for nothing the bundle could lack"),
			MantlePlaceImportOrchestration::ExplainNothingToImport(Build(false, false), Manifest, NoTerrain).IsEmpty());
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOrchestrationActorReplacementTest,
	"MantlePlace.Import.Orchestration.ActorReplacement",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOrchestrationActorReplacementTest::RunTest(const FString& Parameters)
{
	if (GEditor == nullptr)
	{
		AddInfo(TEXT("No editor: actor replacement needs an editor world. Skipped."));
		return true;
	}
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
		FName(TEXT("MantlePlaceOrchestrationTestWorld")));
	if (!TestNotNull(TEXT("a throwaway world to import into"), World))
	{
		return false;
	}

	const FString IdentityA = TEXT("aaaaaaaa-1111");
	const FString IdentityB = TEXT("bbbbbbbb-2222");
	const auto SpawnTagged = [World](const FString& Identity)
	{
		AActor* Actor = World->SpawnActor<AActor>();
		if (Actor != nullptr && !Identity.IsEmpty())
		{
			Actor->Tags.AddUnique(FName(*MantlePlaceImportNaming::ImportTag(Identity)));
		}
		return Actor;
	};
	const auto CountTagged = [World](const FString& Identity)
	{
		const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
		int32 Count = 0;
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			Count += It->Tags.Contains(Tag) ? 1 : 0;
		}
		return Count;
	};

	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();

	AActor* Bystander = SpawnTagged(FString());
	SpawnTagged(IdentityB);
	SpawnTagged(IdentityA);
	SpawnTagged(IdentityA);
	TestEqual(TEXT("fixture: two actors carry order A's tag"), CountTagged(IdentityA), 2);

	// Replacement: the prior import's actors go, matched on the tag, and only theirs.
	TestEqual(TEXT("re-import removes exactly order A's actors"),
		MantlePlaceImportOrchestration::ReplacePriorActors(World, IdentityA, Timeline), 2);
	TestEqual(TEXT("order A's actors are gone"), CountTagged(IdentityA), 0);
	TestEqual(TEXT("another order's actor is untouched"), CountTagged(IdentityB), 1);
	TestTrue(TEXT("an untagged actor the user placed is untouched"), IsValid(Bystander));

	// Idempotent: a second sweep finds nothing, and importing repeatedly does not stack.
	TestEqual(TEXT("a second sweep removes nothing"),
		MantlePlaceImportOrchestration::ReplacePriorActors(World, IdentityA, Timeline), 0);
	for (int32 Round = 0; Round < 3; ++Round)
	{
		MantlePlaceImportOrchestration::ReplacePriorActors(World, IdentityA, Timeline);
		SpawnTagged(IdentityA);
	}
	TestEqual(TEXT("three re-imports in a row leave one generation, not three"), CountTagged(IdentityA), 1);

	World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceOrchestrationAssetWipeTest,
	"MantlePlace.Import.Orchestration.AssetWipe",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceOrchestrationAssetWipeTest::RunTest(const FString& Parameters)
{
	UEditorAssetSubsystem* Assets = GEditor ? GEditor->GetEditorSubsystem<UEditorAssetSubsystem>() : nullptr;
	if (Assets == nullptr)
	{
		AddInfo(TEXT("No editor asset subsystem: the asset wipe needs one. Skipped."));
		return true;
	}

	// A throwaway folder and record, under names no real order can have, removed on the way out.
	const FString IdentityA = TEXT("zwipe001-aaaa");
	const FString IdentityB = TEXT("zwipe001-bbbb"); // same eight characters, so the same folder
	const FString Folder = FString(TEXT("/Game/MantlePlaceOrchestrationTest")) / MantlePlaceImportNaming::ShortIdentity(IdentityA);
	const auto Cleanup = [&]()
	{
		Assets->DeleteDirectory(Folder);
		IFileManager::Get().Delete(*MantlePlaceImportProvenance::RecordPath(IdentityA), false, true, true);
	};
	const auto WriteRecord = [](const FString& Identity)
	{
		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		return MantlePlaceImportProvenance::Write(Record);
	};
	Cleanup();

	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();

	// Nothing there: import freely, nothing to refuse.
	TestTrue(TEXT("no prior import: no refusal"),
		MantlePlaceImportOrchestration::WipePriorAssets(IdentityA, Folder, Timeline).IsEmpty());

	// Content with no record (0.3.0 or earlier): refused, and left alone.
	TestTrue(TEXT("fixture: a content folder"), Assets->MakeDirectory(Folder));
	TestFalse(TEXT("content with no record is refused"),
		MantlePlaceImportOrchestration::WipePriorAssets(IdentityA, Folder, Timeline).IsEmpty());
	TestTrue(TEXT("a refusal leaves the folder"), Assets->DoesDirectoryExist(Folder));

	// Another order's folder (same truncated name): refused, and left alone.
	TestTrue(TEXT("fixture: order B's record"), WriteRecord(IdentityB));
	TestFalse(TEXT("another order's folder is refused"),
		MantlePlaceImportOrchestration::WipePriorAssets(IdentityA, Folder, Timeline).IsEmpty());
	TestTrue(TEXT("the refusal on another order's folder leaves it"), Assets->DoesDirectoryExist(Folder));

	// Same identity: the folder is replaced, and a second wipe finds nothing to do.
	TestTrue(TEXT("fixture: order A's record"), WriteRecord(IdentityA));
	TestTrue(TEXT("same identity: no refusal"),
		MantlePlaceImportOrchestration::WipePriorAssets(IdentityA, Folder, Timeline).IsEmpty());
	TestFalse(TEXT("same identity: the prior folder is wiped"), Assets->DoesDirectoryExist(Folder));
	TestTrue(TEXT("a second wipe is a no-op"),
		MantlePlaceImportOrchestration::WipePriorAssets(IdentityA, Folder, Timeline).IsEmpty());

	Cleanup();
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
