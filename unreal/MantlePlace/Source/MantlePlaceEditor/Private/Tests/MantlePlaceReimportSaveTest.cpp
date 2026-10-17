// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceMeshImporter.h"

#include "Components/StaticMeshComponent.h"
#include "Editor.h"
#include "Engine/Level.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Subsystems/EditorAssetSubsystem.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/StrongObjectPtr.h"

// dcc #349: a re-import over an order whose buildings mesh was SAVED left the new buildings actor
// unsavable ("Illegal reference to private object ... SM_Buildings"), and the buildings were gone
// after a reopen. The wipe's force-delete clears RF_Public and RF_Standalone on the old mesh, and
// when anything still held it, it stayed in memory at its own path; the glTF import that followed
// found it there and imported into it, so the new actor pointed at an object no save may reference.
//
// The round trip the importer runs, minus the bundle: a buildings mesh imported from a glTF and an
// actor spawned for it, both saved; then the asset wipe, the same glTF imported again, the actor
// replaced, and everything saved again. Run in a partitioned level (the actor in its own external
// package, where it was found) and in a non-partitioned one (the actor in the level's package).
// It cannot reopen the level; the live check in the pull request does.

namespace MantlePlaceReimportSaveTest
{
const FString ContentRoot = TEXT("/Game/MantlePlaceReimportSaveTest");

/**
 * The smallest glTF binary the importer takes: one triangle, positions and indices, no material.
 * Written by hand rather than shipped, so the test carries no binary fixture.
 */
TArray<uint8> MinimalGlb()
{
	const FString Json = TEXT(
		"{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
		"\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],"
		"\"buffers\":[{\"byteLength\":44}],"
		"\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36,\"target\":34962},"
		"{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6,\"target\":34963}],"
		"\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[10,10,0]},"
		"{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]}");

	TArray<uint8> JsonChunk;
	const FTCHARToUTF8 Utf8(*Json);
	JsonChunk.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
	while (JsonChunk.Num() % 4 != 0)
	{
		JsonChunk.Add(' ');
	}

	TArray<uint8> BinChunk;
	const float Positions[9] = { 0.f, 0.f, 0.f, 10.f, 0.f, 0.f, 0.f, 10.f, 0.f };
	BinChunk.Append(reinterpret_cast<const uint8*>(Positions), sizeof(Positions));
	const uint16 Indices[3] = { 0, 1, 2 };
	BinChunk.Append(reinterpret_cast<const uint8*>(Indices), sizeof(Indices));
	while (BinChunk.Num() % 4 != 0)
	{
		BinChunk.Add(0);
	}

	TArray<uint8> Out;
	const auto Put32 = [&Out](uint32 Value) { Out.Append(reinterpret_cast<const uint8*>(&Value), 4); };
	Put32(0x46546C67); // "glTF"
	Put32(2);
	Put32(12 + 8 + JsonChunk.Num() + 8 + BinChunk.Num());
	Put32(JsonChunk.Num());
	Put32(0x4E4F534A); // "JSON"
	Out.Append(JsonChunk);
	Put32(BinChunk.Num());
	Put32(0x004E4942); // "BIN\0"
	Out.Append(BinChunk);
	return Out;
}

/** Save one package as File > Save All would. False is what the user saw: the package did not save. */
bool Save(UPackage* Package, UObject* Asset)
{
	if (Package == nullptr)
	{
		return false;
	}
	FSavePackageArgs Args;
	Args.TopLevelFlags = RF_Standalone;
	Args.SaveFlags = SAVE_NoError;
	const FString Filename = FPackageName::LongPackageNameToFilename(Package->GetName(),
		Package->ContainsMap() ? FPackageName::GetMapPackageExtension() : FPackageName::GetAssetPackageExtension());
	return UPackage::SavePackage(Package, Asset, *Filename, Args);
}

/** The package an actor saves into: its own in a partitioned level, the level's in a flat one. */
bool SaveActor(AActor* Actor)
{
	UPackage* External = Actor != nullptr ? Actor->GetExternalPackage() : nullptr;
	if (External != nullptr)
	{
		return Save(External, nullptr);
	}
	return Actor != nullptr && Save(Actor->GetPackage(), Actor->GetWorld());
}

/** The tagged buildings actor: the one the import spawned, as the wipe will find it. */
AStaticMeshActor* SpawnBuildings(UWorld* World, UStaticMesh* Mesh, const FString& Identity)
{
	FMantlePlaceVaultManifest Manifest;
	FString Error;
	AStaticMeshActor* Actor = MantlePlaceMeshImporter::ImportBuildings(World, Manifest, Mesh, Error);
	if (Actor != nullptr)
	{
		Actor->Tags.AddUnique(FName(*MantlePlaceImportNaming::ImportTag(Identity)));
	}
	return Actor;
}

void DeleteOnDisk(const FString& LevelPackageName, const FString& Identity)
{
	// The external actors' folder for every level under the content root, and its objects' twin.
	const FString ExternalActors = FPackageName::GetLongPackagePath(ULevel::GetExternalActorsPath(LevelPackageName));
	const FString ExternalObjects = ExternalActors.Replace(TEXT("/__ExternalActors__/"), TEXT("/__ExternalObjects__/"));
	for (const FString& PackagePath : { ContentRoot, ExternalActors, ExternalObjects })
	{
		const FString Dir = FPaths::ConvertRelativePathToFull(FPackageName::LongPackageNameToFilename(PackagePath / TEXT("")));
		IFileManager::Get().DeleteDirectory(*Dir, /*RequireExists*/ false, /*Tree*/ true);
	}
	IFileManager::Get().Delete(*MantlePlaceImportProvenance::RecordPath(Identity), false, true, true);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceReimportSaveTest,
	"MantlePlace.Import.ReimportSave",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceReimportSaveTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceReimportSaveTest;

	UEditorAssetSubsystem* Assets = GEditor ? GEditor->GetEditorSubsystem<UEditorAssetSubsystem>() : nullptr;
	if (Assets == nullptr)
	{
		AddInfo(TEXT("No editor: the re-import save round trip needs the editor's asset subsystem. Skipped."));
		return true;
	}

	const FString Glb = FPaths::ConvertRelativePathToFull(
		FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ReimportSaveTest") / TEXT("Buildings.glb"));
	if (!TestTrue(TEXT("fixture: the glTF is written"), FFileHelper::SaveArrayToFile(MinimalGlb(), *Glb)))
	{
		return false;
	}

	for (const bool bPartitioned : { true, false })
	{
		const FString Pass = bPartitioned ? TEXT("partitioned") : TEXT("non-partitioned");
		// Names no real order can have; everything they make is deleted again at the end.
		const FString Identity = bPartitioned ? TEXT("zrsavewp-0349") : TEXT("zrsavefl-0349");
		const FString Dest = MantlePlaceImportNaming::ImportRoot(ContentRoot, Identity);
		const FString LevelPackageName = ContentRoot / (bPartitioned ? TEXT("L_ReimportSaveWP") : TEXT("L_ReimportSaveFlat"));
		DeleteOnDisk(LevelPackageName, Identity);

		UWorld::InitializationValues IVS;
		IVS.CreateWorldPartition(bPartitioned).EnableWorldPartitionStreaming(bPartitioned);
		UWorld* World = UWorld::CreateWorld(EWorldType::Editor, /*bInformEngineOfWorld*/ false,
			FName(*FPackageName::GetShortName(LevelPackageName)), CreatePackage(*LevelPackageName),
			/*bAddToRoot*/ true, ERHIFeatureLevel::Num, &IVS);
		if (!TestNotNull(*FString::Printf(TEXT("%s: a throwaway world with a package"), *Pass), World))
		{
			continue;
		}
		// A level saved from the editor is a public, standalone asset; a world made here is neither,
		// and its package would refuse the save for that alone. Cleared again before it is destroyed.
		World->SetFlags(RF_Public | RF_Standalone);
		MantlePlaceImportTiming::FTimeline Timeline;
		Timeline.Start();
		FMantlePlaceVaultManifest Manifest;
		FString Error;

		// --- First import, saved: the mesh is on disk and the actor's package holds a reference to it ---
		UStaticMesh* First = MantlePlaceMeshImporter::ImportMeshAsset(Manifest, Glb, Dest, /*bEnableNanite*/ false, Error);
		AStaticMeshActor* FirstActor = SpawnBuildings(World, First, Identity);
		if (!TestNotNull(*FString::Printf(TEXT("%s: the first import makes the mesh (%s)"), *Pass, *Error), First)
			|| !TestNotNull(*FString::Printf(TEXT("%s: the first import spawns the actor"), *Pass), FirstActor))
		{
			World->ClearFlags(RF_Public | RF_Standalone);
			World->DestroyWorld(false);
			DeleteOnDisk(LevelPackageName, Identity);
			continue;
		}
		const FString MeshPath = First->GetPathName();
		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		Record.ContentRoot = ContentRoot;
		TestTrue(*FString::Printf(TEXT("%s: fixture: the provenance record a successful import writes"), *Pass),
			MantlePlaceImportProvenance::Write(Record));
		TestTrue(*FString::Printf(TEXT("%s: the first mesh saves"), *Pass), Save(First->GetPackage(), First));
		TestTrue(*FString::Printf(TEXT("%s: the first actor saves"), *Pass), SaveActor(FirstActor));

		// What the live editor did: something held a counted reference to the saved mesh, so the
		// force-delete could not free it (LogReferenceChain: "not currently reachable but it does
		// have 1 refcount set"). Held here on purpose; without it the test's garbage collection frees
		// the old mesh and the defect never shows.
		TStrongObjectPtr<UStaticMesh> HeldOldMesh(First);

		// --- Re-import of the same order, in the importer's order: the asset wipe, the glTF, the actors ---
		TestTrue(*FString::Printf(TEXT("%s: the same order's re-import is not refused"), *Pass),
			MantlePlaceImportOrchestration::WipePriorAssets(Identity, Dest, Timeline).IsEmpty());
		Error.Reset();
		UStaticMesh* Second = MantlePlaceMeshImporter::ImportMeshAsset(Manifest, Glb, Dest, /*bEnableNanite*/ false, Error);
		MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline);
		AStaticMeshActor* SecondActor = SpawnBuildings(World, Second, Identity);
		if (TestNotNull(*FString::Printf(TEXT("%s: the re-import makes the mesh (%s)"), *Pass, *Error), Second)
			&& TestNotNull(*FString::Printf(TEXT("%s: the re-import spawns the actor"), *Pass), SecondActor))
		{
			TestEqual(*FString::Printf(TEXT("%s: the re-imported mesh is at the same path"), *Pass), Second->GetPathName(), MeshPath);
			TestTrue(*FString::Printf(TEXT("%s: the re-import made a new mesh, not the deleted one again"), *Pass),
				Second != HeldOldMesh.Get());
			TestFalse(*FString::Printf(TEXT("%s: the deleted mesh no longer sits at the asset's path"), *Pass),
				HeldOldMesh->GetPathName() == MeshPath);
			TestTrue(*FString::Printf(TEXT("%s: the re-imported mesh is public, so another package may reference it"), *Pass),
				Second->HasAnyFlags(RF_Public));
			TestTrue(*FString::Printf(TEXT("%s: the re-imported mesh is standalone, so it saves as an asset"), *Pass),
				Second->HasAnyFlags(RF_Standalone));
			TestTrue(*FString::Printf(TEXT("%s: the new actor points at the re-imported mesh"), *Pass),
				SecondActor->GetStaticMeshComponent()->GetStaticMesh() == Second);

			// --- Saved again: what failed in the user's Save All ---
			TestTrue(*FString::Printf(TEXT("%s: the re-imported mesh saves"), *Pass), Save(Second->GetPackage(), Second));
			TestTrue(*FString::Printf(TEXT("%s: the new buildings actor saves"), *Pass), SaveActor(SecondActor));
			TestTrue(*FString::Printf(TEXT("%s: the mesh is on disk after the save"), *Pass),
				FPackageName::DoesPackageExist(Second->GetPackage()->GetName()));
		}

		World->ClearFlags(RF_Public | RF_Standalone);
		World->DestroyWorld(/*bInformEngineOfDestruction*/ false);
		Assets->DeleteDirectory(Dest);
		DeleteOnDisk(LevelPackageName, Identity);
	}
	IFileManager::Get().DeleteDirectory(*FPaths::GetPath(Glb), /*RequireExists*/ false, /*Tree*/ true);
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
