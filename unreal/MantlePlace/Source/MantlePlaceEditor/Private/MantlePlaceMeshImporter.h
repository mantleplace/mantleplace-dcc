// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceTerrainCollisionLogic.h"

struct FMantlePlaceVaultManifest;
class UWorld;
class AStaticMeshActor;
class UStaticMesh;

namespace MantlePlaceMeshImporter
{
	UStaticMesh* ImportMeshAsset(
		const FMantlePlaceVaultManifest& Manifest,
		const FString& GlbFile,
		const FString& DestPackagePath,
		bool bEnableNanite,
		FString& OutError);

	AStaticMeshActor* Import(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError);

	AStaticMeshActor* ImportBuildings(
		UWorld* World,
		const FMantlePlaceVaultManifest& Manifest,
		UStaticMesh* Mesh,
		FString& OutError);

	void ApplyCollision(AStaticMeshActor* Actor, EMantlePlaceMeshCollision Collision);

	bool RestoreCollisionTurnedOff(AStaticMeshActor* Actor);
}
