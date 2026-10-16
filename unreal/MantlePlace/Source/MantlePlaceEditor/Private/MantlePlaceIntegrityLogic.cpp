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
