// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// The imagery drape: the aerial PNG as a texture and the drape material built from it. It runs
// FIRST, because the landscape adopts the material only if it is set before ALandscape::Import
// builds its components, and the terrain mesh is assigned it right after its own import. It spawns
// no actor of its own; what it reports at the end (Finalize) is about the geometry that used it.

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
		Drape.Sha256 = GetString(D, TEXT("sha256")); // verified against the extracted bytes at import
		// Kept verbatim; the prefix match that gives it meaning lives in the logic unit, and the
		// self-consistency gate that can refuse it runs in CrossCheck, once the heightmap's AOI
		// rectangle is known.
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

		// imagery_drape.sha256 is likewise schema-required — same fail-closed rule as the heightmap.
		if (Drape.bPresent && Drape.Sha256.IsEmpty())
		{
			OutError = TEXT("manifest hosts.unreal.imagery_drape has no sha256 (schema-required); refusing to import unverifiable bytes.");
			return false;
		}
		return true;
	}

	bool CrossCheck(const FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		// The drape's alignment descriptor against the two rectangles the manifest publishes for
		// itself. Nothing is re-derived here: both rectangles are published values, and this only asks
		// whether the bundle's own claim about them is true. A "matches" claim they refute is refused,
		// because that bundle imports looking correct and is silently mis-draped.
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

	// Build the drape material up front: it depends only on the manifest + imagery texture, not the
	// geometry, so the Landscape can be created with it already assigned. Non-fatal.
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
			// Geometry still imports; it just won't get the auto-assigned imagery.
			Ctx.Log.Add(Err);
		}
	}

	// The Landscape adopted the material at creation and meshes were assigned it right after their
	// import, so there is nothing left to assign here -- just report the outcome and sanity-check coverage.
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

			// The manifest's own alignment descriptor, branched on rather than skimmed past. The
			// divergent shape carries the ETL's measured figures and produces a warning; the other
			// shapes produce a note. A "matches" claim the extents refute never reaches here.
			const FString AlignmentLine = FMantlePlaceDrapeAlignmentLogic::DescribeForLog(
				FMantlePlaceDrapeAlignmentLogic::Classify(Manifest.Drape.Alignment));
			if (!AlignmentLine.IsEmpty())
			{
				Ctx.Log.Add(AlignmentLine);
			}

			// Sanity-check imagery coverage against the terrain. The drape is placed at its true
			// geographic footprint; if the bundle's imagery spans only part of the AOI it will not
			// blanket the terrain (a web-ETL data issue, not an import fault).
			if (Manifest.Heightmap.bPresent)
			{
				// Both spans in UE axis order (X = North, Y = East) — GetAoiSizeUeCm owns the swap
				// between the manifest's grid-axis naming and UE's, so this must not open-code it.
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
		// else: a drape was requested but the material failed to build — the error was logged by Import.
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeDrape() { return MakeUnique<FDrapeHandler>(); }
}
