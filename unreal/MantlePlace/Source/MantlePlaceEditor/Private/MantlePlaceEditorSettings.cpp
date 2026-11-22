// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceEditorSettings.h"

#include "MantlePlaceImportNaming.h"

#include "Math/UnrealMathUtility.h"
#include "UObject/UObjectGlobals.h"

UMantlePlaceEditorSettings::UMantlePlaceEditorSettings()
	: GeneratedContentRoot(MantlePlaceImportNaming::DefaultContentRoot())
	, LocalTileServerFirstPort(DefaultLocalTileServerFirstPort)
	, LocalTileServerPortScanCount(DefaultLocalTileServerPortScanCount)
	, MaterializePollIntervalSeconds(DefaultMaterializePollIntervalSeconds)
	, MaterializeMaxPolls(DefaultMaterializeMaxPolls)
	, MaxConsecutivePollFailures(DefaultMaxConsecutivePollFailures)
	, WorldPartitionGridSize(DefaultWorldPartitionGridSize)
{
	FoliageTreeMeshes.Add(FSoftObjectPath(DefaultFoliageTreeMesh()));
	FoliageShrubMeshes.Add(FSoftObjectPath(DefaultFoliageShrubMesh()));
}

int32 UMantlePlaceEditorSettings::UsableLocalTileServerFirstPort(int32 Configured)
{
	return (Configured >= 1024 && Configured <= 65535) ? Configured : DefaultLocalTileServerFirstPort;
}

int32 UMantlePlaceEditorSettings::UsableLocalTileServerPortScanCount(int32 FirstPort, int32 Configured)
{
	if (Configured < 1)
	{
		Configured = DefaultLocalTileServerPortScanCount;
	}
	const int32 Room = 65535 - UsableLocalTileServerFirstPort(FirstPort) + 1;
	return FMath::Clamp(Configured, 1, Room);
}

float UMantlePlaceEditorSettings::UsableMaterializePollIntervalSeconds(float Configured)
{
	return (Configured >= 0.5f && Configured <= 600.0f)
		? Configured
		: DefaultMaterializePollIntervalSeconds;
}

int32 UMantlePlaceEditorSettings::UsableMaterializeMaxPolls(int32 Configured)
{
	return Configured >= 1 ? Configured : DefaultMaterializeMaxPolls;
}

int32 UMantlePlaceEditorSettings::UsableMaxConsecutivePollFailures(int32 Configured)
{
	return Configured >= 0 ? Configured : DefaultMaxConsecutivePollFailures;
}

int32 UMantlePlaceEditorSettings::UsableWorldPartitionGridSize(int32 Configured)
{
	return (Configured >= 1 && Configured <= 16) ? Configured : DefaultWorldPartitionGridSize;
}

void UMantlePlaceEditorSettings::ResolveLocalTileServerPortScan(int32& OutFirstPort, int32& OutPortCount)
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	const int32 ConfiguredFirst =
		Settings != nullptr ? Settings->LocalTileServerFirstPort : DefaultLocalTileServerFirstPort;
	const int32 ConfiguredCount = Settings != nullptr
		? Settings->LocalTileServerPortScanCount
		: DefaultLocalTileServerPortScanCount;

	OutFirstPort = UsableLocalTileServerFirstPort(ConfiguredFirst);
	OutPortCount = UsableLocalTileServerPortScanCount(ConfiguredFirst, ConfiguredCount);
}

float UMantlePlaceEditorSettings::ResolveMaterializePollIntervalSeconds()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaterializePollIntervalSeconds(Settings != nullptr
		? Settings->MaterializePollIntervalSeconds
		: DefaultMaterializePollIntervalSeconds);
}

int32 UMantlePlaceEditorSettings::ResolveMaterializeMaxPolls()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaterializeMaxPolls(
		Settings != nullptr ? Settings->MaterializeMaxPolls : DefaultMaterializeMaxPolls);
}

int32 UMantlePlaceEditorSettings::ResolveMaxConsecutivePollFailures()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaxConsecutivePollFailures(
		Settings != nullptr ? Settings->MaxConsecutivePollFailures : DefaultMaxConsecutivePollFailures);
}

int32 UMantlePlaceEditorSettings::ResolveWorldPartitionGridSize()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableWorldPartitionGridSize(
		Settings != nullptr ? Settings->WorldPartitionGridSize : DefaultWorldPartitionGridSize);
}

FString UMantlePlaceEditorSettings::ResolveGeneratedContentRoot()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return MantlePlaceImportNaming::ResolveContentRoot(
		Settings != nullptr ? Settings->GeneratedContentRoot : FString());
}

FName UMantlePlaceEditorSettings::GetCategoryName() const
{
	return TEXT("Plugins");
}
