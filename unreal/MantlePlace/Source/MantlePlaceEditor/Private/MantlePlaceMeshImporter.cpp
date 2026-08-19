// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceMeshImporter.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"

#include "AssetImportTask.h"
#include "AssetToolsModule.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "IAssetTools.h"
#include "Modules/ModuleManager.h"
#include "UObject/UObjectGlobals.h"

namespace MantlePlaceMeshImporter
{
	static UStaticMesh* ImportGlbAsset(const FString& GlbFile, const FString& DestPath, FString& OutError)
	{
		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = GlbFile;
		Task->DestinationPath = DestPath;
		Task->bAutomated = true;
		Task->bReplaceExisting = true;
		Task->bSave = false;

		TArray<UAssetImportTask*> Tasks;
		Tasks.Add(Task);
		Module.Get().ImportAssetTasks(Tasks);

		// Load every imported object (mesh + embedded textures/materials) before renaming, since
		// renaming invalidates the path strings. Then rename each to the project naming standard.
		TArray<UObject*> Imported;
		for (const FString& Path : Task->ImportedObjectPaths)
		{
			if (UObject* Obj = LoadObject<UObject>(nullptr, *Path))
			{
				Imported.Add(Obj);
			}
		}
		for (UObject* Obj : Imported)
		{
			MantlePlaceImportNaming::RenameToConvention(Obj);
		}

		// Interchange returns the mesh plus its embedded assets in an unspecified order, so
		// select the StaticMesh explicitly rather than trusting index 0.
		for (UObject* Obj : Imported)
		{
			if (UStaticMesh* Mesh = Cast<UStaticMesh>(Obj))
			{
				return Mesh;
			}
		}

		OutError = FString::Printf(TEXT("Interchange imported no StaticMesh from %s."), *GlbFile);
		return nullptr;
	}

	AStaticMeshActor* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		bool bEnableNanite,
		FString& OutError)
	{
		if (World == nullptr)
		{
			OutError = TEXT("No editor world to import the mesh into.");
			return nullptr;
		}

		UStaticMesh* Mesh = ImportGlbAsset(GlbFile, DestPackagePath / TEXT("Mesh"), OutError);
		if (Mesh == nullptr)
		{
			return nullptr;
		}

		if (bEnableNanite && Manifest.bNaniteRecommended)
		{
			// Through the accessors: UStaticMesh::NaniteSettings is UE_DEPRECATED(5.7) and goes
			// private in the next release, so touching the member directly is a compile error there
			// rather than the C4996 it is here. NotifyNaniteSettingsChanged() is the targeted
			// PostEditChangeProperty for this property — a bare PostEditChange() rebuilds more than
			// the Nanite data needs.
			Mesh->Modify();
			FMeshNaniteSettings Settings = Mesh->GetNaniteSettings();
			Settings.bEnabled = true;
			Mesh->SetNaniteSettings(Settings);
			Mesh->NotifyNaniteSettingsChanged();
		}

		AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(Manifest.GetMeshLocation(), FRotator::ZeroRotator);
		if (Actor == nullptr)
		{
			OutError = TEXT("Failed to spawn the StaticMeshActor.");
			return nullptr;
		}

		Actor->SetMobility(EComponentMobility::Static);
		Actor->GetStaticMeshComponent()->SetStaticMesh(Mesh);
		Actor->SetActorLabel(FString::Printf(TEXT("MP_Mesh_%s"), *Manifest.JobId.Left(8)));
		return Actor;
	}

	AStaticMeshActor* ImportBuildings(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		FString& OutError)
	{
		if (World == nullptr)
		{
			OutError = TEXT("No editor world to import the buildings into.");
			return nullptr;
		}

		// Separate subfolder from the terrain mesh so the imported UStaticMesh names don't collide.
		UStaticMesh* Mesh = ImportGlbAsset(GlbFile, DestPackagePath / TEXT("Buildings"), OutError);
		if (Mesh == nullptr)
		{
			return nullptr;
		}

		// Buildings share the terrain's Local Projected Frame (centroid ground at z=0), so the identical
		// GetMeshLocation() transform lands them resting on the terrain. No Nanite (trivial geometry) and
		// no imagery drape (massing is untextured).
		AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(Manifest.GetMeshLocation(), FRotator::ZeroRotator);
		if (Actor == nullptr)
		{
			OutError = TEXT("Failed to spawn the buildings StaticMeshActor.");
			return nullptr;
		}

		Actor->SetMobility(EComponentMobility::Static);
		Actor->GetStaticMeshComponent()->SetStaticMesh(Mesh);
		Actor->SetActorLabel(FString::Printf(TEXT("MP_Buildings_%s"), *Manifest.JobId.Left(8)));
		return Actor;
	}
}
