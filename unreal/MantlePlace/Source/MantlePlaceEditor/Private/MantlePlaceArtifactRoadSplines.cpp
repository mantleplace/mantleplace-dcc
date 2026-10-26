// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// The derived "road_splines" layer of the ODbL Vector/ set (a top-level sibling of `unreal`):
// Z-draped road centerlines -> one spline actor per road (Wave-2 pipeline layers). The Unreal block's
// own copy is read first when the manifest publishes one (`hosts.unreal.road_splines`, MPB 1.16.0): the
// same roads draped on the engine's ground, in the landscape's datum on a US order, where the shared
// layer's heights are not (HPS-52). Failing that the layer is read from its GeoPackage through the
// `gpkg` format row of `vector.layers`, and from its GeoJSON row when the bundle has no GeoPackage row
// (or its GeoPackage cannot be read). Each source is placed only in this host's datum. Like the
// buildings, this is content, not a terrain representation: it imports whenever the bundle ships the
// layer, regardless of Mode, and a failure is non-fatal. Each road is an AMantlePlaceRoadSpline whose
// width, class and name are real properties, so PCG, a Blueprint or a road tool read them by name
// without re-reading the bundle and without parsing anything.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceRoadSpline.h" // runtime: AMantlePlaceRoadSpline
#include "MantlePlaceRoadSplinesGpkg.h"
#include "MantlePlaceRoadSplinesLogic.h"

#include "Components/SplineComponent.h"
#include "Engine/World.h"
#include "Misc/FileHelper.h"

namespace
{
/**
 * Whether a `gpkg` format row holds the layer's lines. A row naming no family is the whole layer; a
 * layer split by family names one per family, and the road lines are the `line` family's.
 */
bool IsLineFamilyRow(const TSharedPtr<FJsonObject>& FormatRow)
{
	const FString Family = MantlePlaceManifestJson::GetString(FormatRow, TEXT("geometry_family"));
	return Family.IsEmpty() || Family.Equals(TEXT("line"), ESearchCase::IgnoreCase);
}

class FRoadSplinesHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("road_splines")); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Roads; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactRoadSplines", "Road splines"); }

	// Both formats the importer takes: the GeoPackage read first, the GeoJSON as the fallback. Read from
	// the top-level `vector.layers`, before the `unreal`-presence check, so streaming-adjacent data
	// survives on unmaterialized bundles.
	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		// This host's own copy (MPB 1.16.0), read from the Unreal block and nowhere else (HPS-33).
		// Every member is verbatim: whether its datum is this host's is FMantlePlaceRoadSplinesLogic's
		// call, and a datum that is not refuses the copy by name at import, never the manifest.
		if (const TSharedPtr<FJsonObject>* EnginePtr = GetObject(In.Unreal, TEXT("road_splines")))
		{
			M.RoadSplines.EnginePath = GetString(*EnginePtr, TEXT("path"));
			M.RoadSplines.EngineSha256 = GetString(*EnginePtr, TEXT("sha256"));
			M.RoadSplines.EngineVerticalDatum = GetString(*EnginePtr, TEXT("vertical_datum"));
			const TArray<TSharedPtr<FJsonValue>>* EngineTables = nullptr;
			if ((*EnginePtr)->TryGetArrayField(TEXT("tables"), EngineTables) && EngineTables != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& TableValue : *EngineTables)
				{
					FString TableName;
					if (TableValue.IsValid() && TableValue->TryGetString(TableName) && !TableName.IsEmpty())
					{
						M.RoadSplines.EngineTables.Add(TableName);
					}
				}
			}
		}
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
							if (!Format.IsValid())
							{
								continue;
							}
							const FString FormatName = GetString(Format, TEXT("format"));
							if (FormatName == TEXT("geojson") && M.RoadSplines.Path.IsEmpty())
							{
								M.RoadSplines.Path = GetString(Format, TEXT("path"));
								M.RoadSplines.Sha256 = GetString(Format, TEXT("sha256"));
							}
							else if (FormatName == TEXT("gpkg") && M.RoadSplines.GpkgPath.IsEmpty() && IsLineFamilyRow(Format))
							{
								M.RoadSplines.GpkgPath = GetString(Format, TEXT("path"));
								M.RoadSplines.GpkgSha256 = GetString(Format, TEXT("sha256"));
								const TArray<TSharedPtr<FJsonValue>>* Tables = nullptr;
								if (Format->TryGetArrayField(TEXT("tables"), Tables) && Tables != nullptr)
								{
									for (const TSharedPtr<FJsonValue>& TableValue : *Tables)
									{
										FString TableName;
										if (TableValue.IsValid() && TableValue->TryGetString(TableName) && !TableName.IsEmpty())
										{
											M.RoadSplines.GpkgTables.Add(TableName);
										}
									}
								}
							}
						}
					}
					break;
				}
			}
		}
		M.RoadSplines.bPresent = !M.RoadSplines.Path.IsEmpty();
		M.RoadSplines.bGpkgPresent = !M.RoadSplines.GpkgPath.IsEmpty();
		M.RoadSplines.bEnginePresent = !M.RoadSplines.EnginePath.IsEmpty();
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override
	{
		return M.RoadSplines.bEnginePresent || M.RoadSplines.bPresent || M.RoadSplines.bGpkgPresent;
	}

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.RoadSplines.bEnginePresent)
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.RoadSplines.EnginePath, M.RoadSplines.EngineSha256 });
		}
		if (M.RoadSplines.bPresent && !M.RoadSplines.Path.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.RoadSplines.Path, M.RoadSplines.Sha256 });
		}
		if (M.RoadSplines.bGpkgPresent && !M.RoadSplines.GpkgPath.IsEmpty())
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.RoadSplines.GpkgPath, M.RoadSplines.GpkgSha256 });
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!IsPresent(Manifest))
		{
			return;
		}
		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0;
		bool bParsed = false;
		// The heights are placed only in this host's datum, the landscape's. A source stating
		// another is a named skip, as HPS-53 skips a file in another frame, and is never shifted
		// (HPS-33): on a US order the shared layer's NAVD88 heights are skipped here whether or not
		// the manifest also publishes the engine copy, which is the one read instead. The skip comes
		// after the integrity pre-check, which hashes every declared payload whether or not it is
		// placed, so a refused file's bytes are still verified and a bad digest still stops the
		// import; what the skip spares is reading the file for placement.
		//
		// The sources are tried in the order HPS-52 gives: the Unreal block's own copy, then the shared
		// layer. A source that cannot be read (missing from the zip, not a GeoPackage, in another
		// frame) says why in the log and gives way to the next, so a layer is never lost to the source
		// that was tried first. The first that reads is the one placed.
		const TArray<FMantlePlaceRoadSplinesCandidate> Plan = Manifest.GetRoadSplinesPlan();
		for (const FMantlePlaceRoadSplinesCandidate& Candidate : Plan)
		{
			if (!Candidate.bPlaceable)
			{
				Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *Candidate.Refusal));
				continue;
			}
			// A source that failed part-way leaves nothing behind for the next one to add to.
			Splines.Reset();
			LinesSeen = 0;
			bParsed = Candidate.Source == EMantlePlaceRoadSplinesSource::EngineCopy
			    ? ReadEngineCopy(Ctx, Splines, LinesSeen)
			    : ReadSharedLayer(Ctx, Splines, LinesSeen);
			if (bParsed)
			{
				break;
			}
		}
		if (!bParsed)
		{
			return;
		}

		int32 SplineIndex = 0;
		for (const FMantlePlaceRoadSpline& Road : Splines)
		{
			AMantlePlaceRoadSpline* SplineActor = Ctx.World->SpawnActor<AMantlePlaceRoadSpline>();
			if (SplineActor == nullptr)
			{
				continue;
			}
			// The actor made its own spline as its root; the published points go in as the world-space
			// positions they already are.
			USplineComponent* SplineComponent = SplineActor->Spline;
			SplineComponent->ClearSplinePoints(/*bUpdateSpline*/ false);
			for (const FVector& Point : Road.PointsUeCm)
			{
				SplineComponent->AddSplinePoint(Point, ESplineCoordinateSpace::World, /*bUpdateSpline*/ false);
			}
			SplineComponent->UpdateSpline();

			// What the platform published, as the actor's own properties: no tag carries any of it.
			SplineActor->EstimatedWidthM = static_cast<float>(Road.WidthMEstimated);
			SplineActor->RoadClass = Road.RoadClass.IsEmpty() ? NAME_None : FName(*Road.RoadClass);
			SplineActor->RoadName = Road.Name;

			// Claim is the whole of the wipe registration: it files the actor under this import's tag,
			// which is what the stale-actor sweep matches whatever the actor's class (an actor of any
			// type, this one included, is replaced on re-import and never stacked).
			Ctx.Claim(SplineActor, MantlePlaceImportNaming::RoadSplineLabel(Ctx.Identity, SplineIndex++));
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

private:
	/**
	 * The Unreal block's own copy (`hosts.unreal.road_splines`); false (with the reason logged) when
	 * it cannot be read, and the shared layer is tried next when it is placeable.
	 */
	static bool ReadEngineCopy(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		return ReadGpkgFile(
		    Ctx, EMantlePlaceRoadSplinesSource::EngineCopy, Block.EnginePath, Block.EngineTables,
		    TEXT(" Trying the shared layer instead."), OutSplines, OutLinesSeen);
	}

	/**
	 * The shared layer: its GeoPackage first. Only that file's absence sends the import to the
	 * GeoJSON, with one more case: a GeoPackage the bundle declares but this read cannot use
	 * (missing from the zip, not a GeoPackage, in another frame) says why in the log and gives way
	 * to the GeoJSON when there is one, so a layer is never lost to the format that was tried first.
	 */
	static bool ReadSharedLayer(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		bool bParsed = false;
		if (Block.bGpkgPresent)
		{
			bParsed = ReadGpkg(Ctx, OutSplines, OutLinesSeen);
		}
		if (!bParsed && Block.bPresent)
		{
			bParsed = ReadGeoJson(Ctx, OutSplines, OutLinesSeen);
		}
		return bParsed;
	}

	/** The shared layer from its GeoPackage; false (with the reason logged) when it cannot be read. */
	static bool ReadGpkg(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		const FString Fallback = Block.bPresent ? TEXT(" Reading the GeoJSON instead.") : TEXT("");
		return ReadGpkgFile(
		    Ctx, EMantlePlaceRoadSplinesSource::SharedLayer, Block.GpkgPath, Block.GpkgTables, Fallback, OutSplines,
		    OutLinesSeen);
	}

	/**
	 * One GeoPackage of road lines, from whichever source names it. Heights are read as the file
	 * carries them: whether they may be placed was decided before this is called.
	 */
	static bool ReadGpkgFile(
	    FMantlePlaceImportContext& Ctx,
	    EMantlePlaceRoadSplinesSource Source,
	    const FString& Path,
	    const TArray<FString>& Tables,
	    const FString& Fallback,
	    TArray<FMantlePlaceRoadSpline>& OutSplines,
	    int32& OutLinesSeen)
	{
		TArray<uint8> GpkgBytes;
		if (!Ctx.ReadBytes(Path, GpkgBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines: bundle is missing %s.%s"), *Path, *Fallback));
			return false;
		}
		int32 RowsUnreadable = 0;
		FString GpkgError;
		if (!FMantlePlaceRoadSplinesGpkg::ReadBytes(
		        GpkgBytes, Tables, Ctx.Manifest.Georef.OriginEastingM, Ctx.Manifest.Georef.OriginNorthingM,
		        Ctx.Manifest.Georef.Epsg, OutSplines, OutLinesSeen, RowsUnreadable, GpkgError))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines: %s could not be read: %s%s"), *Path, *GpkgError, *Fallback));
			return false;
		}
		Ctx.Log.Add(FMantlePlaceRoadSplinesLogic::DescribeSource(Source, Path));
		if (RowsUnreadable > 0)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Road splines: %d feature(s) in %s had a geometry that could not be read and were passed over."),
				RowsUnreadable, *Path));
		}
		return true;
	}

	/** The layer from its GeoJSON; false (with the reason logged) when it cannot be read. */
	static bool ReadGeoJson(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		TArray<uint8> GeoJsonBytes;
		FString GeoJsonText, SplinesError;
		if (!Ctx.ReadBytes(Manifest.RoadSplines.Path, GeoJsonBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: bundle is missing %s."), *Manifest.RoadSplines.Path));
			return false;
		}
		FFileHelper::BufferToString(GeoJsonText, GeoJsonBytes.GetData(), GeoJsonBytes.Num());
		if (!FMantlePlaceRoadSplinesLogic::ParseGeoJson(
		        GeoJsonText, Manifest.Georef.OriginEastingM, Manifest.Georef.OriginNorthingM, Manifest.Georef.Epsg,
		        OutSplines, OutLinesSeen, SplinesError))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *SplinesError));
			return false;
		}
		return true;
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeRoadSplines() { return MakeUnique<FRoadSplinesHandler>(); }
}
