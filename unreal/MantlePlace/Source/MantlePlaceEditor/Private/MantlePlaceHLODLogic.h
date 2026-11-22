// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"
#include "WorldPartition/HLOD/HLODLayer.h"

namespace MantlePlaceHLODLogic
{
TArray<EMantlePlaceLayerFamily> HLODFamilies();

bool HasHLOD(EMantlePlaceLayerFamily Family);

bool ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family);

EHLODLayerType LayerType(EMantlePlaceLayerFamily Family);

FString ExplainBuildRefusal(bool bPartitioned, bool bWorldSaved, int32 UnsavedHLODSourceActors);

FString DescribeBuildCost(int32 HLODActors, double Seconds);

FString PostImportNote(bool bPartitioned, int32 ActorsWithHLODLayer);

FString CommandletArguments(const FString& MapPackageName);
}
