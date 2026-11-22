// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceDrape.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"

#include "AssetImportTask.h"
#include "AssetToolsModule.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/Texture2D.h"
#include "Factories/MaterialInstanceConstantFactoryNew.h"
#include "IAssetTools.h"
#include "Landscape.h"
#include "LandscapeProxy.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Materials/MaterialInterface.h"
#include "Modules/ModuleManager.h"
#include "UObject/UnrealType.h"
#include "UObject/UObjectGlobals.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceDrape, Log, All);

namespace MantlePlaceDrape
{
	static const TCHAR* const DrapeTemplatePath =
		TEXT("/MantlePlace/Material/M_MantlePlace_Drape.M_MantlePlace_Drape");

	UTexture2D* ImportTexture(const FString& ImageryFile, const FString& DestPackagePath, FString& OutError)
	{
		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = ImageryFile;
		Task->DestinationPath = MantlePlaceImportNaming::SubfolderPath(
			DestPackagePath, MantlePlaceImportNaming::ESubfolder::Imagery);
		Task->DestinationName = MantlePlaceImportNaming::TextureName(ImageryFile);
		Task->bAutomated = true;
		Task->bReplaceExisting = true;
		Task->bSave = false;

		TArray<UAssetImportTask*> Tasks;
		Tasks.Add(Task);
		Module.Get().ImportAssetTasks(Tasks);

		for (const FString& Path : Task->ImportedObjectPaths)
		{
			if (UTexture2D* Texture = LoadObject<UTexture2D>(nullptr, *Path))
			{
				Texture->AddressX = TA_Clamp;
				Texture->AddressY = TA_Clamp;
				Texture->UpdateResource();

				return Texture;
			}
		}

		OutError = FString::Printf(TEXT("Failed to import imagery texture from %s."), *ImageryFile);
		return nullptr;
	}

	UMaterialInstanceConstant* CreateDrapeMaterial(
		const FMantlePlaceVaultManifest& Manifest,
		UTexture2D* Texture,
		const FString& DestPackagePath,
		FString& OutError)
	{
		UMaterialInterface* Template = LoadObject<UMaterialInterface>(nullptr, DrapeTemplatePath);
		if (Template == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Drape material template not found at %s; imagery imported but not auto-assigned."),
				DrapeTemplatePath);
			return nullptr;
		}

		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		const FString AssetName = MantlePlaceImportNaming::DrapeMaterialName();
		const FString MicPackage = MantlePlaceImportNaming::SubfolderPath(
			DestPackagePath, MantlePlaceImportNaming::ESubfolder::Imagery);
		const FString MicObjectPath = MantlePlaceImportNaming::ObjectPathIn(MicPackage, AssetName);

		UMaterialInstanceConstant* Mic = LoadObject<UMaterialInstanceConstant>(nullptr, *MicObjectPath);
		if (Mic == nullptr)
		{
			UMaterialInstanceConstantFactoryNew* Factory = NewObject<UMaterialInstanceConstantFactoryNew>();
			Factory->InitialParent = Template;
			Mic = Cast<UMaterialInstanceConstant>(Module.Get().CreateAsset(
				AssetName, MicPackage, UMaterialInstanceConstant::StaticClass(), Factory));
		}
		if (Mic == nullptr)
		{
			OutError = TEXT("Failed to create the drape Material Instance.");
			return nullptr;
		}

		Mic->SetParentEditorOnly(Template);
		Mic->SetTextureParameterValueEditorOnly(FMaterialParameterInfo(TEXT("Drape")), Texture);

		FVector2D UvScale, UvOffset;
		Manifest.GetDrapeUvTransform(UvScale, UvOffset);
		Mic->SetVectorParameterValueEditorOnly(
			FMaterialParameterInfo(TEXT("LandscapeQuadsXY")),
			FLinearColor(
				static_cast<float>(Manifest.Heightmap.ComponentCountY * Manifest.GetQuadsPerComponent()),
				static_cast<float>(Manifest.Heightmap.ComponentCountX * Manifest.GetQuadsPerComponent()),
				0.0f, 0.0f));
		Mic->SetVectorParameterValueEditorOnly(
			FMaterialParameterInfo(TEXT("DrapeUvScale")),
			FLinearColor(static_cast<float>(UvScale.X), static_cast<float>(UvScale.Y), 0.0f, 0.0f));
		Mic->SetVectorParameterValueEditorOnly(
			FMaterialParameterInfo(TEXT("DrapeUvOffset")),
			FLinearColor(static_cast<float>(UvOffset.X), static_cast<float>(UvOffset.Y), 0.0f, 0.0f));
		Mic->PostEditChange();
		return Mic;
	}

	const TCHAR* const LandscapeMaterialPropertyName = TEXT("LandscapeMaterial");

	bool IsLandscapeMaterialPropertyResolvable()
	{
		return FindFProperty<FProperty>(
			ALandscapeProxy::StaticClass(), FName(LandscapeMaterialPropertyName)) != nullptr;
	}

	bool AssignMaterial(AActor* Target, UMaterialInstanceConstant* Mic)
	{
		if (Target == nullptr || Mic == nullptr)
		{
			return false;
		}

		if (ALandscapeProxy* Proxy = Cast<ALandscapeProxy>(Target))
		{
			FProperty* const MaterialProperty =
				FindFProperty<FProperty>(Proxy->GetClass(), FName(LandscapeMaterialPropertyName));
			if (MaterialProperty == nullptr)
			{
				UE_LOG(LogMantlePlaceDrape, Error,
					TEXT("Cannot drape landscape \"%s\": this engine build exposes no \"%s\" property on "
						 "%s. The drape material was not assigned; the geometry is otherwise unaffected. "
						 "This plugin is verified against UE 5.8 — if the engine has renamed the "
						 "property, MantlePlaceDrape::LandscapeMaterialPropertyName is the one place to "
						 "change."),
					*Target->GetName(), LandscapeMaterialPropertyName, *Proxy->GetClass()->GetName());
				return false;
			}
			Proxy->PreEditChange(MaterialProperty);
			Proxy->LandscapeMaterial = Mic;
			FPropertyChangedEvent MaterialChangedEvent(MaterialProperty);
			Proxy->PostEditChangeProperty(MaterialChangedEvent);
			return true;
		}

		if (AStaticMeshActor* MeshActor = Cast<AStaticMeshActor>(Target))
		{
			UStaticMeshComponent* const Component = MeshActor->GetStaticMeshComponent();
			if (Component == nullptr)
			{
				UE_LOG(LogMantlePlaceDrape, Error,
					TEXT("Cannot drape \"%s\": the static mesh actor has no mesh component."),
					*Target->GetName());
				return false;
			}
			Component->SetMaterial(0, Mic);
			return true;
		}

		UE_LOG(LogMantlePlaceDrape, Error,
			TEXT("Cannot drape \"%s\": %s is not an actor type this plugin knows how to drape."),
			*Target->GetName(), *Target->GetClass()->GetName());
		return false;
	}
}
