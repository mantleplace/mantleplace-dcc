// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceLevelFrameLogic.h"
#include "WorldPartition/WorldPartitionHandle.h"

DECLARE_LOG_CATEGORY_EXTERN(LogMantlePlaceImport, Log, All);

class FMantlePlaceImportSteps;

namespace MantlePlaceImportOrchestration
{
using FHandlers = MantlePlaceArtifacts::FHandlers;

bool VerifyAll(
	const FMantlePlaceVaultManifest& Manifest, const FZipArchiveReader& Reader,
	MantlePlaceImportTiming::FTimeline& Timeline, FString& OutError, TArray<FString>* OutNotes = nullptr);

FString ExplainNothingToImport(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest,
	const FMantlePlaceImportSelection& Selection);

FString WipePriorAssets(
	const FString& Identity, const FString& DestPackagePath, MantlePlaceImportTiming::FTimeline& Timeline);

int32 RetireDeletedLeftovers(const FString& DestPackagePath);

int32 ReplacePriorActors(UWorld* World, const FString& Identity, MantlePlaceImportTiming::FTimeline& Timeline,
	TArray<TWeakObjectPtr<AActor>>* OutKeptPartitionActors = nullptr);

int32 ReleaseKeptPartitionActors(UWorld* World, const TArray<TWeakObjectPtr<AActor>>& KeptPartitionActors);

struct FPriorActorLoad
{
	int32 Unloaded = 0;
	int32 Loaded = 0;
	TMap<FGuid, FWorldPartitionReference> References;

	void Release() { References.Empty(); }
};

FString ExplainUnloadedPriorActors(UWorld* World, const FString& Identity);

FString LoadUnloadedPriorActors(UWorld* World, const FString& Identity, FPriorActorLoad& OutLoad);

TArray<FMantlePlaceLevelImportTag> ReadLevelImports(UWorld* World);

void PlanSteps(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest, const FMantlePlaceImportSelection& Selection,
	FMantlePlaceImportSteps& Steps);

TArray<FString> DescribeFormatSkips(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest, const FMantlePlaceImportSelection& Selection);

void RunPrepare(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
void RunImport(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
void RunFinalize(const FHandlers& Handlers, FMantlePlaceImportContext& Context);
}
