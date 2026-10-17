// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// `unreal.mesh_alternative`: Terrain.glb as a static mesh, the alternative to the Landscape (or its
// overlay, in Both mode). Its glTF asset import runs in Prepare, before the undo transaction opens;
// Import only spawns for it.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceDrape.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceMeshImporter.h"

#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Materials/MaterialInstanceConstant.h"

namespace
{
class FTerrainMeshHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return MantlePlaceArtifactId::TerrainMesh(); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Terrain; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactTerrainMesh", "Terrain mesh"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;

		// The pipeline's own readiness report. When the mesh stage produced nothing it records why
		// (e.g. "mesh_not_produced"); surface that so a Mesh request can explain the absence instead
		// of dead-ending. `reason` rides only the absent paths — the schema requires it whenever
		// `present` is false and forbids it otherwise, so an empty string here means "the mesh is
		// present" rather than "the bundle forgot to say".
		//
		// MPB 1.0.0 folded readiness into the host block (`hosts.unreal.readiness`), inside the one
		// subtree this host reads (HPS-33). The retired top-level `dcc_readiness` is NOT consulted in
		// either of its old shapes — a fallback there is how a clean break quietly becomes
		// dual-parsing, and the version gate has already refused every bundle that would carry one.
		if (const TSharedPtr<FJsonObject>* ReadinessPtr = GetObject(In.Unreal, TEXT("readiness")))
		{
			if (const TSharedPtr<FJsonObject>* MeshImportPtr = GetObject(*ReadinessPtr, TEXT("mesh_import")))
			{
				M.Mesh.AbsentReason = GetString(*MeshImportPtr, TEXT("reason"));
			}
		}

		if (const TSharedPtr<FJsonObject>* MeshPtr = GetObject(In.Unreal, TEXT("mesh_alternative")))
		{
			const TSharedPtr<FJsonObject> MeshJson = *MeshPtr;
			M.Mesh.Path = GetString(MeshJson, TEXT("path"));
			M.Mesh.UpAxis = GetString(MeshJson, TEXT("up_axis"));
			M.Mesh.bNaniteRecommended = GetBool(MeshJson, TEXT("nanite_recommended"), false);
			// The schema keeps mesh_alternative.sha256 optional (unlike heightmap/drape), so absent =
			// integrity check skipped rather than a parse failure.
			M.Mesh.Sha256 = GetString(MeshJson, TEXT("sha256"));
			M.Mesh.bPresent = !M.Mesh.Path.IsEmpty();

			if (!CheckUpAxis(TEXT("mesh_alternative"), M.Mesh.UpAxis, OutError))
			{
				return false;
			}
		}
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Mesh.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Mesh.bPresent && !M.Mesh.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.Mesh.Path, M.Mesh.Sha256 });
		}
	}

	bool IsTerrainRepresentation() const override { return true; }

	void ExplainAbsence(const FMantlePlaceVaultManifest& M, TArray<FString>& OutReasons) const override
	{
		if (!M.Mesh.bPresent)
		{
			OutReasons.Add(M.Mesh.AbsentReason.IsEmpty()
				? TEXT("this bundle has no static mesh (Terrain.glb)")
				: FString::Printf(
					TEXT("this bundle has no static mesh (pipeline: %s) — the ETL did not generate a mesh for this AOI"),
					*M.Mesh.AbsentReason));
		}
	}

	void Prepare(FMantlePlaceImportContext& Ctx) override
	{
		if (!Ctx.Manifest.Mesh.bPresent)
		{
			return;
		}
		const MantlePlaceImportTiming::FScopedPhase MeshPhase(
			Ctx.Timeline, MantlePlaceImportTiming::Phase::Artifact, TEXT("terrain mesh (glTF, Nanite)"));
		FString Disk;
		if (Ctx.Extract(Ctx.Manifest.Mesh.Path, Disk, Error))
		{
			Mesh = MantlePlaceMeshImporter::ImportMeshAsset(
				Ctx.Manifest, Disk, Ctx.DestPackagePath, /*bEnableNanite*/ true, Error);
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.Mesh.bPresent)
		{
			// v8 bundles ship Cesium quantized-mesh terrain (Terrain/) instead of a glb, so the
			// Landscape is the primary representation. With the Landscape also selected an absent
			// mesh is informational, not a failure; only a Mesh-only request treats it as one.
			if (Ctx.Selection.Includes(MantlePlaceArtifactId::Landscape()))
			{
				Ctx.Log.Add(TEXT("No static mesh in this bundle (Cesium terrain only) — imported the Landscape."));
			}
			else if (!Manifest.Mesh.AbsentReason.IsEmpty())
			{
				// The pipeline told us why it shipped no mesh — relay it so the failure points back
				// at the ETL (e.g. "mesh_not_produced") rather than reading as an importer fault.
				Ctx.Log.Add(FString::Printf(
					TEXT("Mesh requested but this bundle has no static mesh (pipeline: %s). The ETL did "
						 "not generate a mesh for this AOI — re-export once the mesh stage is producing it."),
					*Manifest.Mesh.AbsentReason));
				Ctx.bAllRequestedSucceeded = false;
			}
			else
			{
				Ctx.Log.Add(TEXT("Mesh requested but the bundle has no static mesh."));
				Ctx.bAllRequestedSucceeded = false;
			}
			return;
		}

		// The asset was imported in Prepare, before the transaction; this only spawns for it.
		FString Err = Error;
		AStaticMeshActor* MeshActor = Mesh != nullptr
			? MantlePlaceMeshImporter::Import(Ctx.World, Manifest, Mesh, Err)
			: nullptr;
		if (MeshActor == nullptr)
		{
			Ctx.Log.Add(FString::Printf(TEXT("Mesh failed: %s"), *Err));
			Ctx.bAllRequestedSucceeded = false;
			return;
		}
		Ctx.DrapeTargets.Add(MeshActor);
		if (Ctx.DrapeMic != nullptr && !MantlePlaceDrape::AssignMaterial(MeshActor, Ctx.DrapeMic))
		{
			// The mesh imported and is in the level; only the imagery is missing. Said in
			// the import log rather than only in the output log, because "the terrain is
			// grey" is what the user actually sees and this is the sentence that explains
			// it. AssignMaterial has already logged which of its refusals this was.
			Ctx.Log.Add(TEXT("WARNING: the imagery drape could not be assigned to the terrain "
						 "mesh — see the output log for which engine property is missing."));
			Ctx.bAllRequestedSucceeded = false;
		}
		Ctx.Claim(MeshActor, MantlePlaceImportNaming::ActorLabel(
			MantlePlaceImportNaming::EActorKind::Mesh, Ctx.Identity));
		Ctx.Result.CreatedActors.Add(MeshActor->GetActorLabel());
		Ctx.Log.Add(TEXT("Mesh (Terrain.glb) created."));
	}

private:
	UStaticMesh* Mesh = nullptr;
	FString Error;
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeTerrainMesh() { return MakeUnique<FTerrainMeshHandler>(); }
}
