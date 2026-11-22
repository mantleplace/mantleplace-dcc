// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

#include "MantlePlaceCuratorExtentLogic.h"

class AMantlePlaceExtentBox;
class UWorld;

namespace MantlePlaceLevelExtent
{
	const TCHAR* NativeGeoreferenceClassPath();

	const TCHAR* CesiumGeoreferenceClassPath();

	FBox LevelBounds(UWorld* World);

	TArray<FMantlePlaceGeoPoint> ConvertCorners(UWorld* World, const FBox& Bounds, bool bNative);

	TArray<FMantlePlaceExtent> ImportExtents(UWorld* World);

	TArray<FMantlePlaceExtentBoxState> ExtentBoxes(UWorld* World);

	FMantlePlaceLevelState Gather(UWorld* World);

	FMantlePlaceResolvedExtent Resolve(UWorld* World);

	UWorld* EditorWorld();

	FString CuratorUrl(const FMantlePlaceResolvedExtent& Resolved);

	AMantlePlaceExtentBox* PlaceExtentBox(UWorld* World);
}
