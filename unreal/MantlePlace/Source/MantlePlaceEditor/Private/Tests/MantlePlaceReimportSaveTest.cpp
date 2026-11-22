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

namespace MantlePlaceReimportSaveTest
{
const FString ContentRoot = TEXT("/Game/MantlePlaceReimportSaveTest");

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
	Put32(0x46546C67);
	Put32(2);
	Put32(12 + 8 + JsonChunk.Num() + 8 + BinChunk.Num());
	Put32(JsonChunk.Num());
	Put32(0x4E4F534A);
	Out.Append(JsonChunk);
	Put32(BinChunk.Num());
	Put32(0x004E4942);
	Out.Append(BinChunk);
	return Out;
}

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

bool SaveActor(AActor* Actor)
{
	UPackage* External = Actor != nullptr ? Actor->GetExternalPackage() : nullptr;
	if (External != nullptr)
	{
		return Save(External, nullptr);
	}
	return Actor != nullptr && Save(Actor->GetPackage(), Actor->GetWorld());
}

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
	const FString ExternalActors = FPackageName::GetLongPackagePath(ULevel::GetExternalActorsPath(LevelPackageName));
	const FString ExternalObjects = ExternalActors.Replace(TEXT("/__ExternalActors__/"), TEXT("/__ExternalObjects__/"));
	for (const FString& PackagePath : { ContentRoot, ExternalActors, ExternalObjects })
	{
		const FString Dir = FPaths::ConvertRelativePathToFull(FPackageName::LongPackageNameToFilename(PackagePath / TEXT("")));
		IFileManager::Get().DeleteDirectory(*Dir, false, true);
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
		const FString Identity = bPartitioned ? TEXT("zrsavewp-0349") : TEXT("zrsavefl-0349");
		const FString Dest = MantlePlaceImportNaming::ImportRoot(ContentRoot, Identity);
		const FString LevelPackageName = ContentRoot / (bPartitioned ? TEXT("L_ReimportSaveWP") : TEXT("L_ReimportSaveFlat"));
		DeleteOnDisk(LevelPackageName, Identity);

		UWorld::InitializationValues IVS;
		IVS.CreateWorldPartition(bPartitioned).EnableWorldPartitionStreaming(bPartitioned);
		UWorld* World = UWorld::CreateWorld(EWorldType::Editor, false,
			FName(*FPackageName::GetShortName(LevelPackageName)), CreatePackage(*LevelPackageName),
			true, ERHIFeatureLevel::Num, &IVS);
		if (!TestNotNull(*FString::Printf(TEXT("%s: a throwaway world with a package"), *Pass), World))
		{
			continue;
		}
		World->SetFlags(RF_Public | RF_Standalone);
		MantlePlaceImportTiming::FTimeline Timeline;
		Timeline.Start();
		FMantlePlaceVaultManifest Manifest;
		FString Error;

		UStaticMesh* First = MantlePlaceMeshImporter::ImportMeshAsset(Manifest, Glb, Dest, false, Error);
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

		TStrongObjectPtr<UStaticMesh> HeldOldMesh(First);

		TestTrue(*FString::Printf(TEXT("%s: the same order's re-import is not refused"), *Pass),
			MantlePlaceImportOrchestration::WipePriorAssets(Identity, Dest, Timeline).IsEmpty());
		Error.Reset();
		UStaticMesh* Second = MantlePlaceMeshImporter::ImportMeshAsset(Manifest, Glb, Dest, false, Error);
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

			TestTrue(*FString::Printf(TEXT("%s: the re-imported mesh saves"), *Pass), Save(Second->GetPackage(), Second));
			TestTrue(*FString::Printf(TEXT("%s: the new buildings actor saves"), *Pass), SaveActor(SecondActor));
			TestTrue(*FString::Printf(TEXT("%s: the mesh is on disk after the save"), *Pass),
				FPackageName::DoesPackageExist(Second->GetPackage()->GetName()));
		}

		World->ClearFlags(RF_Public | RF_Standalone);
		World->DestroyWorld(false);
		Assets->DeleteDirectory(Dest);
		DeleteOnDisk(LevelPackageName, Identity);
	}
	IFileManager::Get().DeleteDirectory(*FPaths::GetPath(Glb), false, true);
	return true;
}

#endif
