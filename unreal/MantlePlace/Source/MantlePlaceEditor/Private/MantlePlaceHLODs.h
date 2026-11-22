// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"

class AActor;
class UHLODLayer;
class UWorld;

namespace MantlePlaceHLODs
{
UHLODLayer* FindLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

UHLODLayer* FindOrCreateLayer(const FString& ContentRoot, EMantlePlaceLayerFamily Family);

bool Assign(AActor* Actor, const FString& ContentRoot, EMantlePlaceLayerFamily Family);

int32 CountAssigned(UWorld* World, const FString& ContentRoot);

int32 CountHLODActors(UWorld* World);

struct FBuildReport
{
	bool bBuilt = false;
	FString Message;
	int32 HLODActors = 0;
	double Seconds = 0.0;
};

FBuildReport Build(UWorld* World);
}
