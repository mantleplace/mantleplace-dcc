// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
class AActor;
class UTexture2D;
class UMaterialInstanceConstant;

namespace MantlePlaceDrape
{
	/** Import Imagery.png (already extracted to disk) as a saveable Texture2D asset. */
	UTexture2D* ImportTexture(const FString& ImageryFile, const FString& DestPackagePath, FString& OutError);

	/**
	 * Create a saveable Material Instance Constant from the drape template, bound to the
	 * imagery texture and the world-space extent so the material maps it onto its true
	 * geographic footprint. Returns nullptr (with OutError) if the template is missing.
	 */
	UMaterialInstanceConstant* CreateDrapeMaterial(
		const FMantlePlaceVaultManifest& Manifest,
		UTexture2D* Texture,
		const FString& DestPackagePath,
		FString& OutError);

	/** Assign the drape MIC to a Landscape (LandscapeMaterial) or a StaticMeshActor (slot 0). */
	void AssignMaterial(AActor* Target, UMaterialInstanceConstant* Mic);
}
