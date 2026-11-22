// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class AActor;
class AMantlePlaceFoliageVolume;
class UDataTable;
class UPCGGraph;
class UTexture2D;
class UWorld;

namespace MantlePlaceFoliageScatter
{
struct FInputs
{
	UDataTable* TreePoints = nullptr;
	UTexture2D* CanopyHeight = nullptr;
	UTexture2D* WaterMask = nullptr;
	UTexture2D* LandCover = nullptr;
	FVector2D AoiSizeCm = FVector2D::ZeroVector;
	double GroundMinZCm = 0.0;
	double GroundMaxZCm = 0.0;
	TArray<FSoftObjectPath> TreeMeshes;
	TArray<FSoftObjectPath> ShrubMeshes;
};

UPCGGraph* BuildGraph(const FString& PackagePath, const FString& AssetName, const FInputs& Inputs, FString& OutError);

AMantlePlaceFoliageVolume* SpawnVolume(UWorld* World, const FInputs& Inputs);

void Generate(AMantlePlaceFoliageVolume* Volume, UPCGGraph* Graph);

TArray<AActor*> CleanupGenerated(UWorld* World, const TArray<AActor*>& Actors);

bool HoldsNothing(const AActor* Actor);

int32 DestroyEmptyPartitionActors(UWorld* World, const TArray<AActor*>& PartitionActors);
}
