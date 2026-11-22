// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceDrape.h"
#include "MantlePlaceDrapeAlignmentLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceManifestJson.h"

#include "Engine/Texture2D.h"
#include "Materials/MaterialInstanceConstant.h"

namespace
{
class FDrapeHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("drape")); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Terrain; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactDrape", "Imagery drape"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		const TSharedPtr<FJsonObject>* DrapePtr = GetObject(In.Unreal, TEXT("imagery_drape"));
		if (DrapePtr == nullptr)
		{
			return true;
		}
		const TSharedPtr<FJsonObject> D = *DrapePtr;
		FMantlePlaceDrapeBlock& Drape = M.Drape;
		Drape.Path = GetString(D, TEXT("source"));
		Drape.Sha256 = GetString(D, TEXT("sha256"));
		Drape.Alignment = GetString(D, TEXT("alignment"));
		const TArray<TSharedPtr<FJsonValue>>* Extent = nullptr;
		if (D->TryGetArrayField(TEXT("extent"), Extent) && Extent != nullptr && Extent->Num() == 4)
		{
			Drape.LeftM = (*Extent)[0]->AsNumber();
			Drape.BottomM = (*Extent)[1]->AsNumber();
			Drape.RightM = (*Extent)[2]->AsNumber();
			Drape.TopM = (*Extent)[3]->AsNumber();
			Drape.bPresent = !Drape.Path.IsEmpty() && (Drape.RightM > Drape.LeftM) && (Drape.TopM > Drape.BottomM);
		}

		if (Drape.bPresent && Drape.Sha256.IsEmpty())
		{
			OutError = TEXT("manifest hosts.unreal.imagery_drape has no sha256 (schema-required); refusing to import unverifiable bytes.");
			return false;
		}
		return true;
	}

	bool CrossCheck(const FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		const FMantlePlaceDrapeAlignment Alignment = FMantlePlaceDrapeAlignmentLogic::Classify(M.Drape.Alignment);
		return FMantlePlaceDrapeAlignmentLogic::CheckAgainstExtents(M, Alignment, OutError);
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Drape.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Drape.bPresent && !M.Drape.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.Drape.Path, M.Drape.Sha256 });
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		if (!Ctx.Manifest.Drape.bPresent)
		{
			return;
		}
		const MantlePlaceImportTiming::FScopedPhase DrapePhase(
			Ctx.Timeline, MantlePlaceImportTiming::Phase::Artifact, TEXT("drape texture + material"));
		FString Err, ImageryDisk;
		if (Ctx.Extract(Ctx.Manifest.Drape.Path, ImageryDisk, Err))
		{
			if (UTexture2D* Texture = MantlePlaceDrape::ImportTexture(ImageryDisk, Ctx.DestPackagePath, Err))
			{
				Ctx.DrapeMic = MantlePlaceDrape::CreateDrapeMaterial(Ctx.Manifest, Texture, Ctx.DestPackagePath, Err);
			}
		}
		if (Ctx.DrapeMic == nullptr)
		{
			Ctx.Log.Add(Err);
		}
	}

	void Finalize(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (Ctx.DrapeTargets.Num() == 0)
		{
			return;
		}
		if (!Manifest.Drape.bPresent)
		{
			Ctx.Log.Add(TEXT("No imagery drape in this bundle (geometry only)."));
		}
		else if (Ctx.DrapeMic != nullptr)
		{
			Ctx.Log.Add(TEXT("Imagery draped onto its geographic footprint."));

			const FString AlignmentLine = FMantlePlaceDrapeAlignmentLogic::DescribeForLog(
				FMantlePlaceDrapeAlignmentLogic::Classify(Manifest.Drape.Alignment));
			if (!AlignmentLine.IsEmpty())
			{
				Ctx.Log.Add(AlignmentLine);
			}

			if (Manifest.Heightmap.bPresent)
			{
				const FVector2D AoiSize = Manifest.GetAoiSizeUeCm();
				const double SpanXcm = AoiSize.X;
				const double SpanYcm = AoiSize.Y;
				FVector2D DrapeMin, DrapeSize;
				Manifest.GetDrapeWorldRect(DrapeMin, DrapeSize);
				if (SpanXcm > 0.0 && SpanYcm > 0.0
					&& (DrapeSize.X < SpanXcm * 0.99 || DrapeSize.Y < SpanYcm * 0.99))
				{
					Ctx.Log.Add(FString::Printf(
						TEXT("WARNING: imagery footprint (%.0f x %.0f m) covers only %.0f%% x %.0f%% of the "
							 "terrain (%.0f x %.0f m) — bundle imagery does not span the AOI (web ETL issue)."),
						DrapeSize.X / 100.0, DrapeSize.Y / 100.0,
						100.0 * DrapeSize.X / SpanXcm, 100.0 * DrapeSize.Y / SpanYcm,
						SpanXcm / 100.0, SpanYcm / 100.0));
				}
			}
		}
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeDrape() { return MakeUnique<FDrapeHandler>(); }
}
