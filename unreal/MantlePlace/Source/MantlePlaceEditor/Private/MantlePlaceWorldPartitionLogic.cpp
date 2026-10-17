// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceWorldPartitionLogic.h"

int32 MantlePlaceWorldPartitionLogic::ProxyCount(int32 ComponentsX, int32 ComponentsY, int32 GridSizeInComponents)
{
	if (ComponentsX <= 0 || ComponentsY <= 0 || GridSizeInComponents <= 0)
	{
		return 0;
	}
	return FMath::DivideAndRoundUp(ComponentsX, GridSizeInComponents)
		* FMath::DivideAndRoundUp(ComponentsY, GridSizeInComponents);
}

TArray<int32> MantlePlaceWorldPartitionLogic::WipeOrder(const TArray<EWipeRank>& Ranks)
{
	TArray<int32> Order;
	Order.Reserve(Ranks.Num());
	for (const EWipeRank Rank : { EWipeRank::StreamingProxy, EWipeRank::Other, EWipeRank::LandscapeParent })
	{
		for (int32 Index = 0; Index < Ranks.Num(); ++Index)
		{
			if (Ranks[Index] == Rank)
			{
				Order.Add(Index);
			}
		}
	}
	return Order;
}

bool MantlePlaceWorldPartitionLogic::ShouldForceNonSpatiallyLoaded(bool bPartitioned, bool bLandscapeFamily)
{
	return bPartitioned && !bLandscapeFamily;
}

FString MantlePlaceWorldPartitionLogic::ExplainUnloadedPriorActors(int32 UnloadedCount)
{
	if (UnloadedCount <= 0)
	{
		return FString();
	}
	return FString::Printf(
		TEXT("Not imported: %d actor(s) from the previous import of this order are in unloaded World "
			 "Partition regions, and a re-import can only replace what is loaded. Load the regions that "
			 "hold the previous import (or load all of them in the World Partition window), then import "
			 "again. Nothing was changed."),
		UnloadedCount);
}
