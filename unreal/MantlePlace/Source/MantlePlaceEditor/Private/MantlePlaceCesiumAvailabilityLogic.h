// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class FJsonObject;
class FJsonValue;

struct FMantlePlaceCesiumTile
{
	int32 Zoom = 0;
	int32 X = 0;
	int32 Y = 0;

	bool operator==(const FMantlePlaceCesiumTile& Other) const
	{
		return Zoom == Other.Zoom && X == Other.X && Y == Other.Y;
	}
};

struct FMantlePlaceCesiumAvailabilityDiff
{
	int64 DeclaredTileCount = 0;

	int64 AbsentTileCount = 0;

	int64 UndeclaredTileCount = 0;
};

struct FMantlePlaceCesiumAvailabilityLogic
{
	static bool ParseTilePath(const FString& TilePath, FMantlePlaceCesiumTile& OutTile);

	static TArray<TSharedPtr<FJsonValue>> BuildAvailability(const TArray<FMantlePlaceCesiumTile>& Tiles);

	static bool IsAvailabilityConsistent(
		const TSharedPtr<FJsonObject>& Root,
		const TArray<FMantlePlaceCesiumTile>& Tiles,
		FMantlePlaceCesiumAvailabilityDiff& OutDiff);

	static bool CorrectAvailability(
		const TSharedPtr<FJsonObject>& Root,
		const TArray<FMantlePlaceCesiumTile>& Tiles,
		FMantlePlaceCesiumAvailabilityDiff& OutDiff);

	static FString DescribeMismatch(
		const FString& JobId, const FMantlePlaceCesiumAvailabilityDiff& Diff, int32 PresentTileCount);
};
