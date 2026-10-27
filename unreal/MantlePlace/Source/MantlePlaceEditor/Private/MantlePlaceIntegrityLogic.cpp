// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceIntegrityLogic.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"

TArray<FMantlePlaceDeclaredArtifact> FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(
    const FMantlePlaceVaultManifest& Manifest)
{
	// The chain is assembled from the handlers, not written out here: each artifact declares its own
	// payloads (and the digest the manifest published for each, from ITS block -- HPS-33), and a new
	// artifact is in the chain by being registered. There is no list in this file to forget it from.
	TArray<FMantlePlaceDeclaredArtifact> Artifacts;
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : MantlePlaceArtifacts::MakeAll())
	{
		Handler->CollectDeclared(Manifest, Artifacts);
	}
	return Artifacts;
}

namespace
{
/** The part a file belongs to, found regardless of case (a reader matches a path so, MPB 2.0). */
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
} // namespace

bool FMantlePlaceIntegrityLogic::LeavesOut(const FMantlePlaceArchiveView& View, const FString& Path)
{
	// Each of these is "cannot tell a selection from corruption", and corruption is the safe reading.
	if (!View.bDeclared || View.Kind.Equals(TEXT("everything"), ESearchCase::CaseSensitive))
	{
		return false;
	}
	const FString* Part = FindPart(View, Path);
	if (Part == nullptr)
	{
		return false;
	}
	// A part id is compared whole, never parsed beyond that (spec/changelog.md, 1.10.0).
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
