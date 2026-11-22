// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceDrape.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceMeshImporter.h"
#include "MantlePlaceTerrainCollisionLogic.h"

#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Landscape.h"
#include "Materials/MaterialInstanceConstant.h"

namespace
{
class FTerrainMeshHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return MantlePlaceArtifactId::TerrainMesh(); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Terrain; }
	FName EquivalenceGroup() const override { return FName(TEXT("terrain")); }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactTerrainMesh", "Terrain mesh"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;

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

	bool Prepares() const override { return true; }

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
				Ctx.Manifest, Disk, Ctx.DestPackagePath, true, Error);
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.Mesh.bPresent)
		{
			if (Ctx.Selection.Includes(MantlePlaceArtifactId::Landscape()))
			{
				Ctx.Log.Add(TEXT("No static mesh in this bundle (Cesium terrain only) — imported the Landscape."));
			}
			else if (!Manifest.Mesh.AbsentReason.IsEmpty())
			{
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
		const bool bLandscapeBuilt = Ctx.DrapeTargets.ContainsByPredicate(
			[](const AActor* Target) { return Target != nullptr && Target->IsA<ALandscape>(); });
		MantlePlaceMeshImporter::ApplyCollision(MeshActor, MantlePlaceTerrainCollisionLogic::ForTerrainMesh(bLandscapeBuilt));
		Ctx.DrapeTargets.Add(MeshActor);
		if (Ctx.DrapeMic != nullptr && !MantlePlaceDrape::AssignMaterial(MeshActor, Ctx.DrapeMic))
		{
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
