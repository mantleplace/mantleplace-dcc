// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceMeshImporter.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"

#include "AssetImportTask.h"
#include "AssetToolsModule.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/CollisionProfile.h"
#include "Engine/World.h"
#include "IAssetTools.h"
#include "Modules/ModuleManager.h"
#include "PhysicsEngine/BodySetup.h"
#include "UObject/UObjectGlobals.h"

namespace MantlePlaceMeshImporter
{
	static UStaticMesh* ImportGlbAsset(const FString& GlbFile, const FString& DestPath, FString& OutError)
	{
		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = GlbFile;
		Task->DestinationPath = DestPath;
		Task->DestinationName = MantlePlaceImportNaming::StaticMeshName(GlbFile);
		Task->bAutomated = true;
		Task->bReplaceExisting = true;
		Task->bSave = false;

		TArray<UAssetImportTask*> Tasks;
		Tasks.Add(Task);
		Module.Get().ImportAssetTasks(Tasks);

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

	static void SetNaniteSettings(UStaticMesh* Mesh, bool bEnableNanite)
	{
		FMeshNaniteSettings Settings = Mesh->GetNaniteSettings();
		bool bChanged = false;
		if (bEnableNanite && !Settings.bEnabled)
		{
			Settings.bEnabled = true;
			bChanged = true;
		}
		if (Settings.bEnabled
			&& (Settings.FallbackTarget != ENaniteFallbackTarget::PercentTriangles || Settings.FallbackPercentTriangles < 1.0f))
		{
			Settings.FallbackTarget = ENaniteFallbackTarget::PercentTriangles;
			Settings.FallbackPercentTriangles = 1.0f;
			bChanged = true;
		}
		if (bChanged)
		{
			Mesh->Modify();
			Mesh->SetNaniteSettings(Settings);
			Mesh->NotifyNaniteSettingsChanged();
		}
	}

	static void SetSurfaceCollision(UStaticMesh* Mesh)
	{
		Mesh->Modify();
		if (Mesh->GetBodySetup() == nullptr)
		{
			Mesh->CreateBodySetup();
		}
		UBodySetup* Body = Mesh->GetBodySetup();
		if (Body == nullptr)
		{
			return;
		}
		Body->Modify();
		Body->RemoveSimpleCollision();
		Body->CollisionTraceFlag = CTF_UseComplexAsSimple;
		Body->InvalidatePhysicsData();
		Body->CreatePhysicsMeshes();
		Mesh->bCustomizedCollision = true;
	}

	UStaticMesh* ImportMeshAsset(
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		bool bEnableNanite,
		FString& OutError)
	{
		const MantlePlaceImportNaming::ESubfolder Subfolder = bEnableNanite
			? MantlePlaceImportNaming::ESubfolder::Mesh
			: MantlePlaceImportNaming::ESubfolder::Buildings;
		UStaticMesh* Mesh = ImportGlbAsset(
			GlbFile, MantlePlaceImportNaming::SubfolderPath(DestPackagePath, Subfolder), OutError);
		if (Mesh == nullptr)
		{
			return nullptr;
		}

		SetNaniteSettings(Mesh, bEnableNanite && Manifest.Mesh.bNaniteRecommended);
		SetSurfaceCollision(Mesh);
		return Mesh;
	}

	AStaticMeshActor* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError)
	{
		if (World == nullptr)
		{
			OutError = TEXT("No editor world to import the mesh into.");
			return nullptr;
		}
		if (Mesh == nullptr)
		{
			OutError = TEXT("No terrain mesh asset to spawn.");
			return nullptr;
		}

		AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(
			Manifest.GetMeshLocation(), FMantlePlaceVaultManifest::GetMeshRotation());
		if (Actor == nullptr)
		{
			OutError = TEXT("Failed to spawn the StaticMeshActor.");
			return nullptr;
		}

		Actor->SetMobility(EComponentMobility::Static);
		Actor->GetStaticMeshComponent()->SetStaticMesh(Mesh);
		ApplyCollision(Actor, MantlePlaceTerrainCollisionLogic::ForTerrainMesh(false));
		return Actor;
	}

	AStaticMeshActor* ImportBuildings(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError)
	{
		if (World == nullptr)
		{
			OutError = TEXT("No editor world to import the buildings into.");
			return nullptr;
		}
		if (Mesh == nullptr)
		{
			OutError = TEXT("No buildings mesh asset to spawn.");
			return nullptr;
		}

		AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(
			Manifest.GetMeshLocation(), FMantlePlaceVaultManifest::GetMeshRotation());
		if (Actor == nullptr)
		{
			OutError = TEXT("Failed to spawn the buildings StaticMeshActor.");
			return nullptr;
		}

		Actor->SetMobility(EComponentMobility::Static);
		Actor->GetStaticMeshComponent()->SetStaticMesh(Mesh);
		ApplyCollision(Actor, MantlePlaceTerrainCollisionLogic::ForBuildings());
		return Actor;
	}

	void ApplyCollision(AStaticMeshActor* Actor, EMantlePlaceMeshCollision Collision)
	{
		UStaticMeshComponent* Component = Actor != nullptr ? Actor->GetStaticMeshComponent() : nullptr;
		if (Component == nullptr)
		{
			return;
		}
		Actor->Modify();
		Component->Modify();
		Component->SetCollisionProfileName(Collision == EMantlePlaceMeshCollision::Surface
			? UCollisionProfile::BlockAll_ProfileName
			: UCollisionProfile::NoCollision_ProfileName);
		if (Collision == EMantlePlaceMeshCollision::None)
		{
			Actor->Tags.AddUnique(MantlePlaceImportNaming::CollisionOffTag());
		}
		else
		{
			Actor->Tags.Remove(MantlePlaceImportNaming::CollisionOffTag());
		}
	}

	bool RestoreCollisionTurnedOff(AStaticMeshActor* Actor)
	{
		const UStaticMeshComponent* Component = Actor != nullptr ? Actor->GetStaticMeshComponent() : nullptr;
		if (Component == nullptr || !Actor->Tags.Contains(MantlePlaceImportNaming::CollisionOffTag())
			|| Component->GetCollisionProfileName() != UCollisionProfile::NoCollision_ProfileName)
		{
			return false;
		}
		ApplyCollision(Actor, EMantlePlaceMeshCollision::Surface);
		return true;
	}
}
