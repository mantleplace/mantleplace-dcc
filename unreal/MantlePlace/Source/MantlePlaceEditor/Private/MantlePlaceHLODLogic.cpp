// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceHLODLogic.h"

TArray<EMantlePlaceLayerFamily> MantlePlaceHLODLogic::HLODFamilies()
{
	return { EMantlePlaceLayerFamily::Terrain, EMantlePlaceLayerFamily::Buildings };
}

bool MantlePlaceHLODLogic::HasHLOD(EMantlePlaceLayerFamily Family)
{
	return HLODFamilies().Contains(Family);
}

bool MantlePlaceHLODLogic::ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family)
{
	return bPartitioned && HasHLOD(Family);
}

EHLODLayerType MantlePlaceHLODLogic::LayerType(EMantlePlaceLayerFamily Family)
{
	return EHLODLayerType::MeshSimplify;
}

FString MantlePlaceHLODLogic::ExplainBuildRefusal(bool bPartitioned, bool bWorldSaved, int32 UnsavedHLODSourceActors)
{
	if (!bPartitioned)
	{
		return TEXT("HLODs are a World Partition feature, and this level is not partitioned: there is nothing to build.");
	}
	if (!bWorldSaved)
	{
		return TEXT("Save the level first (File > Save All): the HLOD build reads the saved level, and this one has never been saved.");
	}
	if (UnsavedHLODSourceActors > 0)
	{
		return FString::Printf(
			TEXT("Save the level first (File > Save All): %d actor(s) with an HLOD layer are unsaved, and the HLOD build leaves out every unsaved actor."),
			UnsavedHLODSourceActors);
	}
	return FString();
}

FString MantlePlaceHLODLogic::DescribeBuildCost(int32 HLODActors, double Seconds)
{
	return FString::Printf(TEXT("HLODs built: %d HLOD actor(s) in %.1f s."), HLODActors, Seconds);
}

FString MantlePlaceHLODLogic::PostImportNote(bool bPartitioned, int32 ActorsWithHLODLayer)
{
	if (!bPartitioned || ActorsWithHLODLayer <= 0)
	{
		return FString();
	}
	return FString::Printf(
		TEXT("HLODs: %d actor(s) are in the Terrain and Buildings HLOD layers, but the import builds no HLODs, because "
			 "the build leaves out unsaved actors. Save (File > Save All), then use Build > Build HLODs, or "
			 "MantlePlaceImporterLibrary.BuildHLODs, which reports the build's cost here."),
		ActorsWithHLODLayer);
}

FString MantlePlaceHLODLogic::CommandletArguments(const FString& MapPackageName)
{
	return FString::Printf(
		TEXT("%s -run=WorldPartitionBuilderCommandlet -Builder=WorldPartitionHLODsBuilder -AllowCommandletRendering -unattended"),
		*MapPackageName);
}
