// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

namespace MantlePlaceWorldPartitionLogic
{
int32 ProxyCount(int32 ComponentsX, int32 ComponentsY, int32 GridSizeInComponents);

enum class EWipeRank : uint8
{
	StreamingProxy,
	Other,
	LandscapeParent,
};

TArray<int32> WipeOrder(const TArray<EWipeRank>& Ranks);

bool ShouldForceNonSpatiallyLoaded(bool bPartitioned, bool bStreamedFamily);

FString ExplainUnloadedPriorActors(int32 UnloadedCount);

FString ExplainUnloadablePriorActors(int32 UnloadedCount, int32 LoadedCount);

bool CellHoldsPriorScatter(const FBox2D& Cell, const TArray<FBox2D>& ScatterFootprints);

struct FDescriptorPackage
{
	bool bInMemory = false;
	bool bDirty = false;
	bool bHoldsLiveActor = false;
};

bool IsDestroyedThisSession(const FDescriptorPackage& Package);
}
