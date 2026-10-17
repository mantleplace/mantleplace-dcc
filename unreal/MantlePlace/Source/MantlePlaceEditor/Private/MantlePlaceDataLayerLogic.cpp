// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceDataLayerLogic.h"

TArray<EMantlePlaceLayerFamily> MantlePlaceDataLayerLogic::AssignableFamilies()
{
	return {
		EMantlePlaceLayerFamily::Terrain,
		EMantlePlaceLayerFamily::Coverage,
		EMantlePlaceLayerFamily::Buildings,
		EMantlePlaceLayerFamily::Roads,
		EMantlePlaceLayerFamily::Foliage,
		EMantlePlaceLayerFamily::PublicRecords,
	};
}

FString MantlePlaceDataLayerLogic::FamilyName(EMantlePlaceLayerFamily Family)
{
	switch (Family)
	{
	case EMantlePlaceLayerFamily::Terrain:       return TEXT("Terrain");
	case EMantlePlaceLayerFamily::Coverage:      return TEXT("Coverage");
	case EMantlePlaceLayerFamily::Buildings:     return TEXT("Buildings");
	case EMantlePlaceLayerFamily::Roads:         return TEXT("Roads");
	case EMantlePlaceLayerFamily::Foliage:       return TEXT("Foliage");
	case EMantlePlaceLayerFamily::PublicRecords: return TEXT("PublicRecords");
	case EMantlePlaceLayerFamily::None:
	default:                                     return FString();
	}
}

bool MantlePlaceDataLayerLogic::ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family)
{
	return bPartitioned && Family != EMantlePlaceLayerFamily::None;
}
