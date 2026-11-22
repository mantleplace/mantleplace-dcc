// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceRoadSpline.h"
#include "MantlePlaceRoadSplinesGpkg.h"
#include "MantlePlaceRoadSplinesLogic.h"

#include "Components/SplineComponent.h"
#include "Engine/World.h"
#include "Misc/FileHelper.h"

namespace
{
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

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
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
		return M.RoadSplines.bEnginePresent || M.RoadSplines.bPresent || M.RoadSplines.bGpkgPresent
			|| M.RoadSplines.bSiteCsvPresent;
	}

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.RoadSplines.bSiteCsvPresent)
		{
			Out.Add(FMantlePlaceDeclaredArtifact{ M.RoadSplines.SiteCsvPath, M.RoadSplines.SiteCsvSha256 });
		}
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
		if (Manifest.RoadSplines.bSiteCsvPresent)
		{
			bParsed = ReadSiteCsv(Ctx, Splines, LinesSeen);
		}
		const TArray<FMantlePlaceRoadSplinesCandidate> Plan =
			Manifest.RoadSplines.bSiteCsvPresent ? TArray<FMantlePlaceRoadSplinesCandidate>() : Manifest.GetRoadSplinesPlan();
		for (const FMantlePlaceRoadSplinesCandidate& Candidate : Plan)
		{
			if (!Candidate.bPlaceable)
			{
				Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s"), *Candidate.Refusal));
				continue;
			}
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
			USplineComponent* SplineComponent = SplineActor->Spline;
			SplineComponent->ClearSplinePoints(false);
			for (const FVector& Point : Road.PointsUeCm)
			{
				SplineComponent->AddSplinePoint(
					Ctx.Manifest.ToLevelFrame(Point), ESplineCoordinateSpace::World, false);
			}
			SplineComponent->UpdateSpline();

			SplineActor->EstimatedWidthM = static_cast<float>(Road.WidthMEstimated);
			SplineActor->RoadClass = Road.RoadClass.IsEmpty() ? NAME_None : FName(*Road.RoadClass);
			SplineActor->RoadName = Road.Name;

			Ctx.Claim(SplineActor, MantlePlaceImportNaming::RoadSplineLabel(Ctx.Identity, SplineIndex++));
			Ctx.Result.CreatedActors.Add(SplineActor->GetActorLabel());
		}
		Ctx.Log.Add(FMantlePlaceRoadSplinesLogic::DescribeOutcome(LinesSeen, Splines.Num()));
		if (SplineIndex < Splines.Num())
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Road splines: %d spline actor(s) could not be spawned."), Splines.Num() - SplineIndex));
		}
	}

private:
	static bool ReadSiteCsv(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		TArray<uint8> CsvBytes;
		if (!Ctx.ReadBytes(Block.SiteCsvPath, CsvBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: bundle is missing %s."), *Block.SiteCsvPath));
			return false;
		}
		FString CsvText, CsvError;
		FFileHelper::BufferToString(CsvText, CsvBytes.GetData(), CsvBytes.Num());
		int32 RowsUnreadable = 0;
		if (!FMantlePlaceRoadSplinesLogic::ParseSiteCsv(
		        CsvText, Block.SiteHeightM, OutSplines, OutLinesSeen, RowsUnreadable, CsvError))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Road splines skipped: %s %s"), *Block.SiteCsvPath, *CsvError));
			return false;
		}
		Ctx.Log.Add(FString::Printf(TEXT("Road splines read from %s."), *Block.SiteCsvPath));
		if (RowsUnreadable > 0)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Road splines: %d row(s) in %s had no numeric x, y or z and were passed over."),
				RowsUnreadable, *Block.SiteCsvPath));
		}
		return true;
	}

	static bool ReadEngineCopy(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		return ReadGpkgFile(
		    Ctx, EMantlePlaceRoadSplinesSource::EngineCopy, Block.EnginePath, Block.EngineTables,
		    TEXT(" Trying the shared layer instead."), OutSplines, OutLinesSeen);
	}

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

	static bool ReadGpkg(FMantlePlaceImportContext& Ctx, TArray<FMantlePlaceRoadSpline>& OutSplines, int32& OutLinesSeen)
	{
		const FMantlePlaceRoadSplinesBlock& Block = Ctx.Manifest.RoadSplines;
		const FString Fallback = Block.bPresent ? TEXT(" Reading the GeoJSON instead.") : TEXT("");
		return ReadGpkgFile(
		    Ctx, EMantlePlaceRoadSplinesSource::SharedLayer, Block.GpkgPath, Block.GpkgTables, Fallback, OutSplines,
		    OutLinesSeen);
	}

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
