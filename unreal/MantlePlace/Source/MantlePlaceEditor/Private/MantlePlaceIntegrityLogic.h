// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

struct FMantlePlaceDeclaredArtifact
{
	FString Path;

	FString Sha256;

	bool IsVerifiable() const { return !Sha256.IsEmpty(); }
};

struct FMantlePlaceArchiveView
{
	bool bDeclared = false;

	FString Kind;

	TArray<FString> Parts;

	TMap<FString, FString> FileParts;
};

struct FMantlePlaceIntegrityLogic
{
	static TArray<FMantlePlaceDeclaredArtifact> CollectDeclaredArtifacts(
	    const FMantlePlaceVaultManifest& Manifest);

	static TArray<FString> VerifiablePaths(const FMantlePlaceVaultManifest& Manifest);

	static bool LeavesOut(const FMantlePlaceArchiveView& View, const FString& Path);
	static bool LeavesOut(const FMantlePlaceVaultManifest& Manifest, const FString& Path);

	static bool LeavesOutAll(const FMantlePlaceVaultManifest& Manifest, const TArray<FString>& Paths);

	static bool HoldsCesiumTerrain(const FMantlePlaceVaultManifest& Manifest);
};
