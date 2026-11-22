// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"

class AActor;
class UDataLayerAsset;
class UDataLayerInstance;
class UWorld;

namespace MantlePlaceDataLayers
{
UDataLayerAsset* FindAsset(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

UDataLayerInstance* FindOrCreate(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

UDataLayerInstance* FindOdbl(UWorld* World, const FString& ContentRoot);

bool AssignOdbl(AActor* Actor, const FString& ContentRoot);

UDataLayerInstance* Find(UWorld* World, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

bool Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family);
}
