// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

enum class EMantlePlaceLayerFamily : uint8
{
	None,
	Terrain,
	Coverage,
	Buildings,
	Roads,
	Foliage,
	PublicRecords,
};

namespace MantlePlaceDataLayerLogic
{
TArray<EMantlePlaceLayerFamily> AssignableFamilies();

FString FamilyName(EMantlePlaceLayerFamily Family);

bool ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family);
}
