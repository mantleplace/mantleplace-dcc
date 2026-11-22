// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceDataLayerLogic.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceIntegrityLogic.h"

class AActor;
class FJsonObject;
class FMantlePlaceImportSteps;
class UDataTable;
class UTexture2D;
class FZipArchiveReader;
class UMaterialInstanceConstant;
class UWorld;
struct FMantlePlaceVaultManifest;
namespace MantlePlaceImportTiming
{
class FTimeline;
}

struct FMantlePlaceImportSelection
{
	TSet<FName> Excluded;

	bool Includes(FName ArtifactId) const { return !Excluded.Contains(ArtifactId); }

	static FMantlePlaceImportSelection Everything() { return FMantlePlaceImportSelection(); }

	static FMantlePlaceImportSelection FromMode(EMantlePlaceImportMode Mode, bool bFoliageScatter = false);
};

namespace MantlePlaceArtifactId
{
FName Landscape();
FName TerrainMesh();
FName FoliageScatter();
}

struct FMantlePlaceParseInput
{
	const TSharedPtr<FJsonObject>& Root;
	const TSharedPtr<FJsonObject>& Unreal;
};

struct FMantlePlaceImportContext
{
	FMantlePlaceImportContext(
		UWorld* InWorld, const FMantlePlaceVaultManifest& InManifest, const FZipArchiveReader* InReader,
		const FMantlePlaceImportSelection& InSelection, MantlePlaceImportTiming::FTimeline& InTimeline,
		FMantlePlaceImportResult& InResult, TArray<FString>& InLog)
		: World(InWorld), Manifest(InManifest), Reader(InReader), Selection(InSelection)
		, Timeline(InTimeline), Result(InResult), Log(InLog)
	{
	}

	UWorld* World;
	const FMantlePlaceVaultManifest& Manifest;
	const FZipArchiveReader* Reader;
	const FMantlePlaceImportSelection& Selection;
	MantlePlaceImportTiming::FTimeline& Timeline;
	FMantlePlaceImportResult& Result;
	TArray<FString>& Log;

	FString Identity;
	FString FrameTag;
	FString TempDir;
	FString DestPackagePath;
	FString ContentRoot;

	EMantlePlaceLayerFamily Family = EMantlePlaceLayerFamily::None;

	TArray<FString> SourcePaths;
	FName EquivalenceGroup;

	UMaterialInstanceConstant* DrapeMic = nullptr;
	TArray<AActor*> DrapeTargets;

	TMap<FString, UTexture2D*> CoverageTextures;
	UDataTable* TreePointsTable = nullptr;

	bool bAllRequestedSucceeded = true;

	FMantlePlaceImportSteps* Steps = nullptr;

	TSet<FName> Cut;

	bool ReadBytes(const FString& InZipPath, TArray<uint8>& OutBytes) const;

	bool Extract(const FString& InZipPath, FString& OutDiskPath, FString& OutError) const;

	void Claim(AActor* Actor, const FString& Label) const;
};

class IMantlePlaceArtifactHandler
{
public:
	virtual ~IMantlePlaceArtifactHandler() = default;

	virtual FName Id() const = 0;

	virtual FText DisplayName() const = 0;

	virtual bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& Manifest, FString& OutError) const = 0;

	virtual bool CrossCheck(const FMantlePlaceVaultManifest& Manifest, FString& OutError) const { return true; }

	virtual bool IsPresent(const FMantlePlaceVaultManifest& Manifest) const = 0;

	virtual void CollectDeclared(const FMantlePlaceVaultManifest& Manifest, TArray<FMantlePlaceDeclaredArtifact>& Out) const = 0;

	virtual void CollectSourcePaths(const FMantlePlaceVaultManifest& Manifest, TArray<FString>& Out) const
	{
		TArray<FMantlePlaceDeclaredArtifact> Declared;
		CollectDeclared(Manifest, Declared);
		for (const FMantlePlaceDeclaredArtifact& Artifact : Declared)
		{
			Out.Add(Artifact.Path);
		}
	}

	virtual FName EquivalenceGroup() const { return NAME_None; }

	virtual EMantlePlaceLayerFamily LayerFamily() const = 0;

	virtual bool IsTerrainRepresentation() const { return false; }

	virtual void ExplainAbsence(const FMantlePlaceVaultManifest& Manifest, TArray<FString>& OutReasons) const {}

	virtual void Prepare(FMantlePlaceImportContext& Context) {}

	virtual bool Prepares() const { return false; }

	virtual void Import(FMantlePlaceImportContext& Context) = 0;

	virtual void Finalize(FMantlePlaceImportContext& Context) {}
};

namespace MantlePlaceArtifacts
{
using FHandlers = TArray<TUniquePtr<IMantlePlaceArtifactHandler>>;

FHandlers MakeAll();
}
