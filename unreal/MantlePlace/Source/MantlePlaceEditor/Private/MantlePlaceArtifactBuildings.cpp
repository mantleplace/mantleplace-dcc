// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceMeshImporter.h"

#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"

namespace
{
class FBuildingsHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("buildings")); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Buildings; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactBuildings", "Buildings"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		if (const TSharedPtr<FJsonObject>* BuildingsMeshPtr = GetObject(In.Unreal, TEXT("buildings_mesh")))
		{
			const TSharedPtr<FJsonObject> B = *BuildingsMeshPtr;
			M.Buildings.Path = GetString(B, TEXT("path"));
			M.Buildings.UpAxis = GetString(B, TEXT("up_axis"));
			M.Buildings.bPresent = !M.Buildings.Path.IsEmpty();

			if (!CheckUpAxis(TEXT("buildings_mesh"), M.Buildings.UpAxis, OutError))
			{
				return false;
			}
		}
		if (M.Buildings.bPresent)
		{
			if (const TSharedPtr<FJsonObject>* BuildingsPtr = GetObject(In.Root, TEXT("buildings")))
			{
				const TArray<TSharedPtr<FJsonValue>>* Formats = nullptr;
				if ((*BuildingsPtr)->TryGetArrayField(TEXT("formats"), Formats) && Formats != nullptr)
				{
					for (const TSharedPtr<FJsonValue>& Entry : *Formats)
					{
						const TSharedPtr<FJsonObject> Obj = Entry->AsObject();
						if (Obj.IsValid() && GetString(Obj, TEXT("path")) == M.Buildings.Path)
						{
							M.Buildings.Sha256 = GetString(Obj, TEXT("sha256"));
							break;
						}
					}
				}
			}
		}
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Buildings.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Buildings.bPresent && !M.Buildings.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.Buildings.Path, M.Buildings.Sha256 });
		}
	}

	bool Prepares() const override { return true; }

	void Prepare(FMantlePlaceImportContext& Ctx) override
	{
		if (!Ctx.Manifest.Buildings.bPresent)
		{
			return;
		}
		const MantlePlaceImportTiming::FScopedPhase BuildingsPhase(
			Ctx.Timeline, MantlePlaceImportTiming::Phase::Artifact, TEXT("buildings mesh (glTF)"));
		FString Disk;
		if (Ctx.Extract(Ctx.Manifest.Buildings.Path, Disk, Error))
		{
			Mesh = MantlePlaceMeshImporter::ImportMeshAsset(
				Ctx.Manifest, Disk, Ctx.DestPackagePath, false, Error);
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		if (!Ctx.Manifest.Buildings.bPresent)
		{
			return;
		}
		FString Err = Error;
		AStaticMeshActor* BuildingsActor = Mesh != nullptr
			? MantlePlaceMeshImporter::ImportBuildings(Ctx.World, Ctx.Manifest, Mesh, Err)
			: nullptr;
		if (BuildingsActor != nullptr)
		{
			Ctx.Claim(BuildingsActor, MantlePlaceImportNaming::ActorLabel(
				MantlePlaceImportNaming::EActorKind::Buildings, Ctx.Identity));
			Ctx.Result.CreatedActors.Add(BuildingsActor->GetActorLabel());
			Ctx.Log.Add(TEXT("Buildings (Buildings.glb) created."));
		}
		else
		{
			Ctx.Log.Add(FString::Printf(TEXT("Buildings import skipped: %s"), *Err));
		}
	}

private:
	UStaticMesh* Mesh = nullptr;
	FString Error;
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeBuildings() { return MakeUnique<FBuildingsHandler>(); }
}
