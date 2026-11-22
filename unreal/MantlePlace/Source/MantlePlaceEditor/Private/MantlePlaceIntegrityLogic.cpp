// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceIntegrityLogic.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"

TArray<FMantlePlaceDeclaredArtifact> FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(
    const FMantlePlaceVaultManifest& Manifest)
{
	TArray<FMantlePlaceDeclaredArtifact> Artifacts;
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : MantlePlaceArtifacts::MakeAll())
	{
		Handler->CollectDeclared(Manifest, Artifacts);
	}
	return Artifacts;
}

namespace
{
const FString* FindPart(const FMantlePlaceArchiveView& View, const FString& Path)
{
	if (const FString* Exact = View.FileParts.Find(Path))
	{
		return Exact;
	}
	for (const TPair<FString, FString>& Pair : View.FileParts)
	{
		if (Pair.Key.Equals(Path, ESearchCase::IgnoreCase))
		{
			return &Pair.Value;
		}
	}
	return nullptr;
}
}

bool FMantlePlaceIntegrityLogic::LeavesOut(const FMantlePlaceArchiveView& View, const FString& Path)
{
	if (!View.bDeclared || View.Kind.Equals(TEXT("everything"), ESearchCase::CaseSensitive))
	{
		return false;
	}
	const FString* Part = FindPart(View, Path);
	if (Part == nullptr)
	{
		return false;
	}
	return !View.Parts.ContainsByPredicate(
	    [Part](const FString& Held) { return Held.Equals(*Part, ESearchCase::CaseSensitive); });
}

bool FMantlePlaceIntegrityLogic::LeavesOut(const FMantlePlaceVaultManifest& Manifest, const FString& Path)
{
	return LeavesOut(Manifest.Archive, Path);
}

bool FMantlePlaceIntegrityLogic::LeavesOutAll(const FMantlePlaceVaultManifest& Manifest, const TArray<FString>& Paths)
{
	if (Paths.Num() == 0)
	{
		return false;
	}
	for (const FString& Path : Paths)
	{
		if (!LeavesOut(Manifest, Path))
		{
			return false;
		}
	}
	return true;
}

bool FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(const FMantlePlaceVaultManifest& Manifest)
{
	if (!Manifest.Cesium.bHasTerrain)
	{
		return false;
	}
	const FMantlePlaceArchiveView& View = Manifest.Archive;
	if (!View.bDeclared || View.Kind.Equals(TEXT("everything"), ESearchCase::CaseSensitive))
	{
		return true;
	}
	return View.Parts.ContainsByPredicate(
	    [](const FString& Held)
	    {
		    FString Token;
		    FString Level;
		    if (!Held.Split(TEXT("@"), &Token, &Level))
		    {
			    Token = Held;
		    }
		    return Token.Equals(TEXT("terrain.cesium"), ESearchCase::CaseSensitive);
	    });
}

TArray<FString> FMantlePlaceIntegrityLogic::VerifiablePaths(const FMantlePlaceVaultManifest& Manifest)
{
	TArray<FString> Paths;
	for (const FMantlePlaceDeclaredArtifact& Artifact : CollectDeclaredArtifacts(Manifest))
	{
		if (Artifact.IsVerifiable())
		{
			Paths.Add(Artifact.Path);
		}
	}
	return Paths;
}
