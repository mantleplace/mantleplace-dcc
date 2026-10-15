// Copyright Mantle Place. All Rights Reserved.

// The derived "road_splines" layer of the ODbL Vector/ set (a top-level sibling of `unreal`):
// Z-draped road centerlines -> one spline actor per road (Wave-2 pipeline layers). Like the
// buildings, this is content, not a terrain representation: it imports whenever the bundle ships the
// layer, regardless of Mode, and a failure is non-fatal. Width/class/name land as actor tags so PCG
// or a road tool can consume them without re-reading the bundle.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceRoadSplinesLogic.h"

#include "Components/SplineComponent.h"
#include "Engine/World.h"
#include "Misc/FileHelper.h"

namespace
{
class FRoadSplinesHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("road_splines")); }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactRoadSplines", "Road splines"); }

	// The importer consumes the GeoJSON format only; a gpkg-only bundle (base tier) is treated as not
	// shipping splines. Read from the top-level `vector.layers`, before the `unreal`-presence check,
	// so streaming-adjacent data survives on unmaterialized bundles.
	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		if (const TSharedPtr<FJsonObject>* VectorPtr = GetObject(In.Root, TEXT("vector")))
		{
			const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
			if ((*VectorPtr)->TryGetArrayField(TEXT("layers"), Layers) && Layers != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& LayerValue : *Layers)
				{
					const TSharedPtr<FJsonObject> Layer = LayerValue->AsObject();
					if (!Layer.IsValid() || GetString(Layer, TEXT("name")) != TEXT("road_splines"))
					{
						continue;
					}
					// The datum of the layer's heights (MPB 1.8.0), verbatim. Whether it is this host's
					// is FMantlePlaceRoadSplinesLogic's call: a datum that is not refuses the layer by
					// name at import, never the manifest.
					M.RoadSplines.VerticalDatum = GetString(Layer, TEXT("vertical_datum"));
					const TArray<TSharedPtr<FJsonValue>>* Formats = nullptr;
					if (Layer->TryGetArrayField(TEXT("formats"), Formats) && Formats != nullptr)
					{
						for (const TSharedPtr<FJsonValue>& FormatValue : *Formats)
						{
							const TSharedPtr<FJsonObject> Format = FormatValue->AsObject();
							if (Format.IsValid() && GetString(Format, TEXT("format")) == TEXT("geojson"))
							{
								M.RoadSplines.Path = GetString(Format, TEXT("path"));
								M.RoadSplines.Sha256 = GetString(Format, TEXT("sha256"));
								break;
							}
						}
					}
					break;
				}
			}
		}
		M.RoadSplines.bPresent = !M.RoadSplines.Path.IsEmpty();
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.RoadSplines.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.RoadSplines.bPresent && !M.RoadSplines.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.RoadSplines.Path, M.RoadSplines.Sha256 });
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.RoadSplines.bPresent)
		{
			return;
		}
		TArray<uint8> GeoJsonBytes;
		FString GeoJsonText, SplinesError;
		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0;
		bool bParsed = false;
		// The layer's heights are placed only in this host's datum, the landscape's. A layer stating
		// another is a named skip, as HPS-53 skips a file in another frame, and is never shifted
		// (HPS-33). The skip comes after the integrity pre-check, which hashes every declared payload
		// whether or not it is placed, so a refused layer's bytes are still verified and a bad digest
		// still stops the import; what the skip spares is reading the file for placement.
		FString DatumRefusal;
		if (!FMantlePlaceRoadSplinesLogic::CanPlaceHeights(Manifest.GetRoadSplinesDatum(), DatumRefusal))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *DatumRefusal));
		}
		else if (!Ctx.ReadBytes(Manifest.RoadSplines.Path, GeoJsonBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: bundle is missing %s."), *Manifest.RoadSplines.Path));
		}
		else
		{
			FFileHelper::BufferToString(GeoJsonText, GeoJsonBytes.GetData(), GeoJsonBytes.Num());
			bParsed = FMantlePlaceRoadSplinesLogic::ParseGeoJson(
				GeoJsonText, Manifest.Georef.OriginEastingM, Manifest.Georef.OriginNorthingM, Manifest.Georef.Epsg,
				Splines, LinesSeen, SplinesError);
			if (!bParsed)
			{
				Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *SplinesError));
			}
		}
		if (!bParsed)
		{
			return;
		}

		int32 SplineIndex = 0;
		for (const FMantlePlaceRoadSpline& Spline : Splines)
		{
			AActor* SplineActor = Ctx.World->SpawnActor<AActor>();
			if (SplineActor == nullptr)
			{
				continue;
			}
			USplineComponent* SplineComponent = NewObject<USplineComponent>(
				SplineActor, USplineComponent::StaticClass(), TEXT("RoadSpline"), RF_Transactional);
			SplineActor->SetRootComponent(SplineComponent);
			SplineActor->AddInstanceComponent(SplineComponent);
			SplineComponent->RegisterComponent();

			SplineComponent->ClearSplinePoints(/*bUpdateSpline*/ false);
			for (const FVector& Point : Spline.PointsUeCm)
			{
				SplineComponent->AddSplinePoint(Point, ESplineCoordinateSpace::World, /*bUpdateSpline*/ false);
			}
			SplineComponent->UpdateSpline();

			Ctx.Claim(SplineActor, MantlePlaceImportNaming::RoadSplineLabel(Ctx.Identity, SplineIndex++));
			SplineActor->Tags.Add(FName(*FString::Printf(TEXT("width_m=%.1f"), Spline.WidthMEstimated)));
			if (!Spline.RoadClass.IsEmpty())
			{
				SplineActor->Tags.Add(FName(*FString::Printf(TEXT("class=%s"), *Spline.RoadClass)));
			}
			if (!Spline.Name.IsEmpty())
			{
				SplineActor->Tags.Add(FName(*FString::Printf(TEXT("name=%s"), *Spline.Name)));
			}
			Ctx.Result.CreatedActors.Add(SplineActor->GetActorLabel());
		}
		// A layer whose roads all failed to place must not read like an AOI with no roads. The
		// outcome is about the parse (what could be placed); a spawn failure is a different
		// cause and gets its own line rather than borrowing the projection reason.
		Ctx.Log.Add(FMantlePlaceRoadSplinesLogic::DescribeOutcome(LinesSeen, Splines.Num()));
		if (SplineIndex < Splines.Num())
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Road splines: %d spline actor(s) could not be spawned."), Splines.Num() - SplineIndex));
		}
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeRoadSplines() { return MakeUnique<FRoadSplinesHandler>(); }
}
