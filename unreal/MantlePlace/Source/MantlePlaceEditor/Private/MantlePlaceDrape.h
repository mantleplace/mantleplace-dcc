// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
class AActor;
class UTexture2D;
class UMaterialInstanceConstant;

namespace MantlePlaceDrape
{
	UTexture2D* ImportTexture(const FString& ImageryFile, const FString& DestPackagePath, FString& OutError);

	UMaterialInstanceConstant* CreateDrapeMaterial(
		const FMantlePlaceVaultManifest& Manifest,
		UTexture2D* Texture,
		const FString& DestPackagePath,
		FString& OutError);

	bool AssignMaterial(AActor* Target, UMaterialInstanceConstant* Mic);

	extern const TCHAR* const LandscapeMaterialPropertyName;

	bool IsLandscapeMaterialPropertyResolvable();
}
