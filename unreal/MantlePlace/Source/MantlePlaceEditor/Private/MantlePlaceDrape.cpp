// Copyright Mantle Place. All Rights Reserved.

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

namespace MantlePlaceDrape
{
	// The drape material template (authored in-editor). Loaded at runtime; a missing template
	// is non-fatal — the geometry still imports, just without auto-assigned imagery.
	static const TCHAR* const DrapeTemplatePath =
		TEXT("/MantlePlace/Material/M_MantlePlace_Drape.M_MantlePlace_Drape");

	UTexture2D* ImportTexture(const FString& ImageryFile, const FString& DestPackagePath, FString& OutError)
	{
		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));

		UAssetImportTask* Task = NewObject<UAssetImportTask>();
		Task->Filename = ImageryFile;
		Task->DestinationPath = DestPackagePath / TEXT("Imagery");
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
				// The drape material projects this imagery by absolute world position onto the
				// imagery's true geographic footprint. Where the terrain extends beyond that
				// footprint the UVs leave [0,1]; the import default (TA_Wrap) would TILE the imagery
				// across the rest of the terrain. Clamp so it sits once on its footprint with no
				// seams (and is a no-op once the imagery spans the full AOI).
				Texture->AddressX = TA_Clamp;
				Texture->AddressY = TA_Clamp;
				Texture->UpdateResource();

				MantlePlaceImportNaming::RenameToConvention(Texture);
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

		const FString AssetName = FString::Printf(TEXT("MI_Drape_%s"), *Manifest.JobId.Left(8));
		const FString MicPackage = DestPackagePath / TEXT("Imagery");
		const FString MicObjectPath = FString::Printf(TEXT("%s/%s.%s"), *MicPackage, *AssetName, *AssetName);

		// Idempotent: reuse the MIC on re-import (CreateAsset can't prompt-overwrite unattended).
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

		// Geometry-local drape parameters. The material reads LandscapeLayerCoords (grid-quad coords),
		// normalises to [0,1] over the AOI grid via LandscapeQuadsXY, then maps onto the imagery footprint
		// with DrapeUvScale/DrapeUvOffset. There are no world-space ExtentMin/ExtentSize any more, so the
		// imagery rides the surface through sculpts/moves/the ESU Y-mirror instead of clamping to a smear
		// when the geometry leaves a fixed world band. GetDrapeUvTransform is identity for v8 (imagery
		// spans the full AOI); a sub-AOI imagery footprint maps to its correct sub-rect.
		FVector2D UvScale, UvOffset;
		Manifest.GetDrapeUvTransform(UvScale, UvOffset);
		Mic->SetVectorParameterValueEditorOnly(
			FMaterialParameterInfo(TEXT("LandscapeQuadsXY")),
			FLinearColor(
				static_cast<float>(Manifest.ComponentCountX * Manifest.GetQuadsPerComponent()),
				static_cast<float>(Manifest.ComponentCountY * Manifest.GetQuadsPerComponent()),
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

	void AssignMaterial(AActor* Target, UMaterialInstanceConstant* Mic)
	{
		if (Target == nullptr || Mic == nullptr)
		{
			return;
		}

		if (ALandscapeProxy* Proxy = Cast<ALandscapeProxy>(Target))
		{
			// Mirror ALandscapeProxy::EditorSetLandscapeMaterial: run the full Pre/PostEditChange
			// cycle for the *named* LandscapeMaterial property. PostEditChangeProperty's override runs
			// UpdateAllComponentMaterialInstances() and recreates each component's render state — a
			// bare assignment + generic PostEditChange() leaves the components on the previous
			// material. We inline it (rather than calling EditorSetLandscapeMaterial) because that
			// setter symbol is not exported from the Landscape module; Pre/PostEditChange are UObject
			// virtuals (vtable dispatch), so this links cleanly. NOTE: this reliably re-skins an
			// already-finalized landscape; for a *freshly created* one the material must be set BEFORE
			// ALandscape::Import (see MantlePlaceLandscapeImporter::Import) — assigning it on the
			// import frame does not take at render time. The vault importer therefore drapes the
			// landscape at creation and only uses this path for static meshes.
			FProperty* const MaterialProperty =
				FindFieldChecked<FProperty>(Proxy->GetClass(), FName(TEXT("LandscapeMaterial")));
			Proxy->PreEditChange(MaterialProperty);
			Proxy->LandscapeMaterial = Mic;
			FPropertyChangedEvent MaterialChangedEvent(MaterialProperty);
			Proxy->PostEditChangeProperty(MaterialChangedEvent);
		}
		else if (AStaticMeshActor* MeshActor = Cast<AStaticMeshActor>(Target))
		{
			MeshActor->GetStaticMeshComponent()->SetMaterial(0, Mic);
		}
	}
}
