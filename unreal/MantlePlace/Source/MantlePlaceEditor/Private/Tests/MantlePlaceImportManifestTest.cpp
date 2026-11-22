// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDrapeAlignmentLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceRoadSplinesLogic.h"
#include "MantlePlaceTreePointsLogic.h"
#include "MantlePlaceVaultTypes.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "Serialization/JsonWriter.h"

namespace
{
using namespace MantlePlaceConformanceCorpus;

const TCHAR* const ConsumedExpectationKeys[] = {
	TEXT("jobId"),
	TEXT("orderId"),
	TEXT("deliveryModel"),
	TEXT("epsg"),
	TEXT("planetShape"),
	TEXT("hasHeightmap"),
	TEXT("hasDrape"),
	TEXT("hasMesh"),
	TEXT("hasBuildings"),
	TEXT("hasRoadSplines"),
	TEXT("meshAbsentReason"),
	TEXT("integrityCheckedMesh"),
	TEXT("heightmapPath"),
	TEXT("heightmapSha256"),
	TEXT("resolution"),
	TEXT("sectionSizeQuads"),
	TEXT("sectionsPerComponent"),
	TEXT("componentCountX"),
	TEXT("componentCountY"),
	TEXT("row0IsNorth"),
	TEXT("heightmapRangeM"),
	TEXT("heightmapRangeToleranceM"),
	TEXT("drapePath"),
	TEXT("drapeSha256"),
	TEXT("meshPath"),
	TEXT("meshUpAxis"),
	TEXT("meshSha256"),
	TEXT("buildingsPath"),
	TEXT("buildingsUpAxis"),
	TEXT("buildingsSha256"),
	TEXT("roadSplinesPath"),
	TEXT("roadSplinesSha256"),
	TEXT("hasFoliagePoints"),
	TEXT("foliagePointsPath"),
	TEXT("foliagePointsSha256"),
	TEXT("foliagePointsPointCount"),
	TEXT("integrityCheckedFoliagePoints"),
	TEXT("integrityVerifiedPaths"),
	TEXT("drapeAlignment"),
	TEXT("drapeAlignmentShape"),
	TEXT("drapeAlignmentWarns"),
	TEXT("drapeAlignmentMeasured"),
	TEXT("drapeAlignmentMeasuredTolerance"),
	TEXT("hasLandscapeLayers"),
	TEXT("landscapeLayerNames"),
	TEXT("landscapeLayers"),
	TEXT("materialWeightBands"),
	TEXT("cesiumTerrainPath"),
	TEXT("cesiumTerrainPrefix"),
	TEXT("quadsPerComponent"),
	TEXT("landscapeScale"),
	TEXT("landscapeScaleTolerance"),
	TEXT("landscapeSpawnLocation"),
	TEXT("landscapeSpawnToleranceCm"),
	TEXT("meshLocation"),
	TEXT("meshLocationToleranceCm"),
	TEXT("drapeWorldRectMin"),
	TEXT("drapeWorldRectMinToleranceCm"),
	TEXT("drapeWorldRectSize"),
	TEXT("drapeWorldRectSizeToleranceCm"),
	TEXT("publicRecordFamilies"),
	TEXT("publicRecordPaths"),
	TEXT("publicRecordVerifiedPaths"),
	TEXT("publicRecordPointers"),
	TEXT("publicRecordSkippedSources"),
	TEXT("publicRecordFrames"),
};

void AssertStringExpectation(
	FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, const FString& Actual)
{
	FString Expected;
	if (WantsString(Case, Key, Expected))
	{
		T.TestEqual(Case.What(Key), Actual, Expected);
	}
}

void AssertBoolExpectation(FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, bool bActual)
{
	bool bExpected = false;
	if (WantsBool(Case, Key, bExpected))
	{
		T.TestTrue(
			FString::Printf(TEXT("[%s] %s is %s"), *Case.Id, Key, bExpected ? TEXT("true") : TEXT("false")),
			bActual == bExpected);
	}
}

void AssertIntExpectation(FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, int32 Actual)
{
	int32 Expected = 0;
	if (WantsInt(Case, Key, Expected))
	{
		T.TestEqual(Case.What(Key), Actual, Expected);
	}
}

void AssertTupleExpectation(
	FAutomationTestBase& T,
	const FCase& Case,
	const TCHAR* Key,
	const TCHAR* ToleranceKey,
	TConstArrayView<double> Actual)
{
	TArray<double> Expected;
	if (!WantsNumbers(Case, Key, Actual.Num(), Expected))
	{
		return;
	}
	const double Tolerance = ToleranceOr(Case, ToleranceKey, 1e-6);
	for (int32 Index = 0; Index < Expected.Num(); ++Index)
	{
		T.TestEqual(
			FString::Printf(TEXT("[%s] %s[%d]"), *Case.Id, Key, Index),
			Actual[Index],
			Expected[Index],
			Tolerance);
	}
}

void AssertStringArrayExpectation(
	FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, const TArray<FString>& Actual)
{
	TArray<FString> Expected;
	if (!WantsStringArray(Case, Key, Expected))
	{
		return;
	}
	T.TestEqual(Case.What(Key), Actual.Num(), Expected.Num());
	for (int32 Index = 0; Index < FMath::Min(Actual.Num(), Expected.Num()); ++Index)
	{
		T.TestEqual(FString::Printf(TEXT("[%s] %s[%d]"), *Case.Id, Key, Index), Actual[Index], Expected[Index]);
	}
}

void AssertValueMapping(
	FAutomationTestBase& T,
	const FCase& Case,
	const FString& Path,
	const TSharedPtr<FJsonObject>& Mapping,
	const FMantlePlaceRasterValueMapping& Actual)
{
	double Number = 0.0;
	bool bFlag = false;
	FString Text;

	if (ExpectRowNumber(Case, Path, Mapping, TEXT("min"), Number))
	{
		T.TestEqual(Path + TEXT(".min"), Actual.MinValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("max"), Number))
	{
		T.TestEqual(Path + TEXT(".max"), Actual.MaxValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("nodataValue"), Number))
	{
		T.TestTrue(Path + TEXT(".nodataValue is recognised as declared"), Actual.bHasNodata);
		T.TestEqual(Path + TEXT(".nodataValue"), Actual.NodataValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("trueValue"), Number))
	{
		T.TestEqual(Path + TEXT(".trueValue"), Actual.TrueValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("falseValue"), Number))
	{
		T.TestEqual(Path + TEXT(".falseValue"), Actual.FalseValue, Number, 1e-9);
	}
	if (ExpectRowString(Case, Path, Mapping, TEXT("toValue"), Text))
	{
		T.TestEqual(Path + TEXT(".toValue"), Actual.ToValueFormula, Text);
	}
	if (ExpectRowString(Case, Path, Mapping, TEXT("units"), Text))
	{
		T.TestEqual(Path + TEXT(".units"), Actual.Units, Text);
	}
	if (ExpectRowBool(Case, Path, Mapping, TEXT("identity"), bFlag))
	{
		T.TestTrue(Path + TEXT(".identity"), Actual.bIdentity == bFlag);
	}

	const TArray<TSharedPtr<FJsonValue>>* Bands = nullptr;
	if (ExpectRowArray(Case, Path, Mapping, TEXT("bands"), Bands) && Bands != nullptr)
	{
		T.TestEqual(Path + TEXT(".bands count"), Actual.Bands.Num(), Bands->Num());
		for (int32 Index = 0; Index < Bands->Num(); ++Index)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.bands[%d]"), *Path, Index);
			if (ExpectElementString(Case, ElementPath, (*Bands)[Index], Text))
			{
				T.TestEqual(ElementPath, Actual.Bands.IsValidIndex(Index) ? Actual.Bands[Index] : FString(), Text);
			}
		}
	}

	const TArray<TSharedPtr<FJsonValue>>* Classes = nullptr;
	if (ExpectRowArray(Case, Path, Mapping, TEXT("classes"), Classes) && Classes != nullptr)
	{
		T.TestEqual(Path + TEXT(".classes count"), Actual.Classes.Num(), Classes->Num());
		for (int32 Index = 0; Index < Classes->Num(); ++Index)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.classes[%d]"), *Path, Index);
			if (ExpectElementNumber(Case, ElementPath, (*Classes)[Index], Number))
			{
				T.TestEqual(ElementPath, Actual.Classes.IsValidIndex(Index) ? Actual.Classes[Index] : 0,
					static_cast<int32>(Number));
			}
		}
	}
}

void AssertLandscapeLayers(FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	TArray<TSharedPtr<FJsonObject>> Rows;
	if (!WantsObjectRows(Case, TEXT("landscapeLayers"), Rows))
	{
		return;
	}
	T.TestEqual(Case.What(TEXT("landscape layer count")), M.LandscapeLayers.Layers.Num(), Rows.Num());

	for (int32 Index = 0; Index < Rows.Num(); ++Index)
	{
		const FString RowPath = FString::Printf(TEXT("landscapeLayers[%d]"), Index);
		const TSharedPtr<FJsonObject>& Row = Rows[Index];
		FString Text;

		const FMantlePlaceLandscapeLayer* Layer =
			M.LandscapeLayers.Layers.IsValidIndex(Index) ? &M.LandscapeLayers.Layers[Index] : nullptr;

		if (ExpectRowString(Case, RowPath, Row, TEXT("name"), Text))
		{
			T.TestEqual(RowPath + TEXT(".name"), Layer != nullptr ? Layer->Name : FString(), Text);
			T.TestTrue(RowPath + TEXT(" is findable by name"), M.FindLandscapeLayer(*Text) == Layer);
		}
		if (ExpectRowString(Case, RowPath, Row, TEXT("path"), Text))
		{
			T.TestEqual(RowPath + TEXT(".path"), Layer != nullptr ? Layer->Path : FString(), Text);
		}
		if (ExpectRowString(Case, RowPath, Row, TEXT("sha256"), Text))
		{
			T.TestEqual(RowPath + TEXT(".sha256"), Layer != nullptr ? Layer->Sha256 : FString(), Text);
		}

		const TArray<TSharedPtr<FJsonValue>>* Materials = nullptr;
		if (ExpectRowArray(Case, RowPath, Row, TEXT("materials"), Materials) && Materials != nullptr)
		{
			T.TestEqual(RowPath + TEXT(".materials count"),
				Layer != nullptr ? Layer->Materials.Num() : 0, Materials->Num());
			for (int32 Band = 0; Band < Materials->Num(); ++Band)
			{
				const FString ElementPath = FString::Printf(TEXT("%s.materials[%d]"), *RowPath, Band);
				if (ExpectElementString(Case, ElementPath, (*Materials)[Band], Text))
				{
					T.TestEqual(ElementPath,
						(Layer != nullptr && Layer->Materials.IsValidIndex(Band)) ? Layer->Materials[Band] : FString(),
						Text);
				}
			}
		}

		const TArray<TSharedPtr<FJsonValue>>* UeReady = nullptr;
		if (!ExpectRowArray(Case, RowPath, Row, TEXT("ueReady"), UeReady) || UeReady == nullptr)
		{
			continue;
		}
		T.TestEqual(RowPath + TEXT(".ueReady count"),
			Layer != nullptr ? Layer->UeReady.Num() : 0, UeReady->Num());

		for (int32 Companion = 0; Companion < UeReady->Num(); ++Companion)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.ueReady[%d]"), *RowPath, Companion);
			const TSharedPtr<FJsonObject> Element = ExpectElementObject((*UeReady)[Companion]);
			if (!Element.IsValid())
			{
				continue;
			}
			const FMantlePlaceUeReadyRaster* Raster =
				(Layer != nullptr && Layer->UeReady.IsValidIndex(Companion)) ? &Layer->UeReady[Companion] : nullptr;

			double Number = 0.0;
			if (ExpectRowString(Case, ElementPath, Element, TEXT("path"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".path"), Raster != nullptr ? Raster->Path : FString(), Text);
			}
			if (ExpectRowString(Case, ElementPath, Element, TEXT("sha256"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".sha256"), Raster != nullptr ? Raster->Sha256 : FString(), Text);
			}
			if (ExpectRowString(Case, ElementPath, Element, TEXT("encoding"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".encoding"), Raster != nullptr ? Raster->Encoding : FString(), Text);
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("width"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".width"), Raster != nullptr ? Raster->Width : 0,
					static_cast<int32>(Number));
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("height"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".height"), Raster != nullptr ? Raster->Height : 0,
					static_cast<int32>(Number));
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("sizeBytes"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".sizeBytes"), Raster != nullptr ? Raster->SizeBytes : 0,
					static_cast<int64>(Number));
			}

			TSharedPtr<FJsonObject> Mapping;
			if (ExpectRowObject(Case, ElementPath, Element, TEXT("mapping"), Mapping))
			{
				static const FMantlePlaceRasterValueMapping Empty;
				AssertValueMapping(T, Case, ElementPath + TEXT(".mapping"), Mapping,
					Raster != nullptr ? Raster->ValueMapping : Empty);
			}
		}
	}
}

void AssertMaterialWeightBands(
	FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	TArray<TSharedPtr<FJsonObject>> Rows;
	if (!WantsObjectRows(Case, TEXT("materialWeightBands"), Rows))
	{
		return;
	}

	TArray<FMantlePlaceWeightBand> Bands;
	FString Error;
	const FMantlePlaceLandscapeLayer* Layer = M.FindLandscapeLayer(TEXT("material_weights"));
	if (Layer == nullptr)
	{
		T.AddError(Case.What(TEXT("no material_weights layer to resolve bands from")));
		return;
	}
	T.TestTrue(Case.What(TEXT("the band legend resolves")),
		FMantlePlaceLandscapeWeightsLogic::ResolveBands(*Layer, Bands, Error));
	T.TestEqual(Case.What(TEXT("one band per material")), Bands.Num(), Rows.Num());

	for (int32 Index = 0; Index < Rows.Num(); ++Index)
	{
		const FString RowPath = FString::Printf(TEXT("materialWeightBands[%d]"), Index);
		const FMantlePlaceWeightBand* Band = Bands.IsValidIndex(Index) ? &Bands[Index] : nullptr;
		FString Text;
		double Number = 0.0;

		if (ExpectRowString(Case, RowPath, Rows[Index], TEXT("material"), Text))
		{
			T.TestEqual(RowPath + TEXT(".material"), Band != nullptr ? Band->Material : FString(), Text);
		}
		if (ExpectRowString(Case, RowPath, Rows[Index], TEXT("image"), Text))
		{
			T.TestEqual(RowPath + TEXT(".image"), Band != nullptr ? Band->ImagePath : FString(), Text);
		}
		if (ExpectRowNumber(Case, RowPath, Rows[Index], TEXT("channel"), Number))
		{
			T.TestEqual(RowPath + TEXT(".channel"), Band != nullptr ? Band->Channel : -1,
				static_cast<int32>(Number));
		}
	}
}

const TCHAR* AlignmentShapeName(EMantlePlaceDrapeAlignment Shape)
{
	switch (Shape)
	{
	case EMantlePlaceDrapeAlignment::Absent:       return TEXT("absent");
	case EMantlePlaceDrapeAlignment::Matches:      return TEXT("matches");
	case EMantlePlaceDrapeAlignment::Unverified:   return TEXT("unverified");
	case EMantlePlaceDrapeAlignment::Diverges:     return TEXT("diverges");
	case EMantlePlaceDrapeAlignment::Unrecognised: return TEXT("unrecognised");
	}
	return TEXT("?");
}

void AssertExpectations(FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	AssertStringExpectation(T, Case, TEXT("jobId"), M.JobId);
	AssertStringExpectation(T, Case, TEXT("orderId"), M.OrderId);
	AssertStringExpectation(T, Case, TEXT("deliveryModel"), M.DeliveryModel);
	AssertStringExpectation(T, Case, TEXT("planetShape"), M.Georef.PlanetShape);
	AssertStringExpectation(T, Case, TEXT("meshAbsentReason"), M.Mesh.AbsentReason);
	AssertStringExpectation(T, Case, TEXT("heightmapPath"), M.Heightmap.Path);
	AssertStringExpectation(T, Case, TEXT("heightmapSha256"), M.Heightmap.Sha256);
	AssertStringExpectation(T, Case, TEXT("drapePath"), M.Drape.Path);
	AssertStringExpectation(T, Case, TEXT("drapeSha256"), M.Drape.Sha256);
	AssertStringExpectation(T, Case, TEXT("meshPath"), M.Mesh.Path);
	AssertStringExpectation(T, Case, TEXT("meshUpAxis"), M.Mesh.UpAxis);
	AssertStringExpectation(T, Case, TEXT("meshSha256"), M.Mesh.Sha256);
	AssertStringExpectation(T, Case, TEXT("buildingsPath"), M.Buildings.Path);
	AssertStringExpectation(T, Case, TEXT("buildingsUpAxis"), M.Buildings.UpAxis);
	AssertStringExpectation(T, Case, TEXT("buildingsSha256"), M.Buildings.Sha256);
	AssertStringExpectation(T, Case, TEXT("roadSplinesPath"), M.RoadSplines.Path);
	AssertStringExpectation(T, Case, TEXT("roadSplinesSha256"), M.RoadSplines.Sha256);
	AssertStringExpectation(T, Case, TEXT("foliagePointsPath"), M.Foliage.Path);
	AssertStringExpectation(T, Case, TEXT("foliagePointsSha256"), M.Foliage.Sha256);
	AssertStringExpectation(T, Case, TEXT("drapeAlignment"), M.Drape.Alignment);
	AssertStringExpectation(T, Case, TEXT("cesiumTerrainPath"), M.Cesium.TerrainPath);
	AssertStringExpectation(
		T, Case, TEXT("cesiumTerrainPrefix"),
		MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(M.Cesium.TerrainPath));

	AssertBoolExpectation(T, Case, TEXT("hasHeightmap"), M.Heightmap.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasDrape"), M.Drape.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasMesh"), M.Mesh.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasBuildings"), M.Buildings.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasRoadSplines"), M.RoadSplines.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasFoliagePoints"), M.Foliage.bPresent);
	AssertBoolExpectation(T, Case, TEXT("hasLandscapeLayers"), M.LandscapeLayers.bPresent);
	AssertBoolExpectation(T, Case, TEXT("row0IsNorth"), M.Heightmap.bRow0IsNorth);
	AssertBoolExpectation(T, Case, TEXT("integrityCheckedMesh"), !M.Mesh.Sha256.IsEmpty());

	AssertIntExpectation(T, Case, TEXT("epsg"), M.Georef.Epsg);
	AssertIntExpectation(T, Case, TEXT("quadsPerComponent"), M.GetQuadsPerComponent());
	AssertIntExpectation(T, Case, TEXT("resolution"), M.Heightmap.Resolution);
	AssertIntExpectation(T, Case, TEXT("sectionSizeQuads"), M.Heightmap.SectionSizeQuads);
	AssertIntExpectation(T, Case, TEXT("sectionsPerComponent"), M.Heightmap.SectionsPerComponent);
	AssertIntExpectation(T, Case, TEXT("componentCountX"), M.Heightmap.ComponentCountX);
	AssertIntExpectation(T, Case, TEXT("componentCountY"), M.Heightmap.ComponentCountY);

	AssertTupleExpectation(T, Case, TEXT("heightmapRangeM"), TEXT("heightmapRangeToleranceM"),
		{ M.Heightmap.ZMinM, M.Heightmap.ZMaxM });

	const FVector Scale = M.GetLandscapeScale();
	AssertTupleExpectation(T, Case, TEXT("landscapeScale"), TEXT("landscapeScaleTolerance"),
		{ Scale.X, Scale.Y, Scale.Z });

	const FVector Spawn = M.GetLandscapeSpawnLocation();
	AssertTupleExpectation(T, Case, TEXT("landscapeSpawnLocation"), TEXT("landscapeSpawnToleranceCm"),
		{ Spawn.X, Spawn.Y, Spawn.Z });

	const FVector MeshLocation = M.GetMeshLocation();
	AssertTupleExpectation(T, Case, TEXT("meshLocation"), TEXT("meshLocationToleranceCm"),
		{ MeshLocation.X, MeshLocation.Y, MeshLocation.Z });

	FVector2D DrapeMin, DrapeSize;
	M.GetDrapeWorldRect(DrapeMin, DrapeSize);
	AssertTupleExpectation(T, Case, TEXT("drapeWorldRectMin"), TEXT("drapeWorldRectMinToleranceCm"),
		{ DrapeMin.X, DrapeMin.Y });
	AssertTupleExpectation(T, Case, TEXT("drapeWorldRectSize"), TEXT("drapeWorldRectSizeToleranceCm"),
		{ DrapeSize.X, DrapeSize.Y });

	TArray<FString> LayerNames;
	for (const FMantlePlaceLandscapeLayer& Layer : M.LandscapeLayers.Layers)
	{
		LayerNames.Add(Layer.Name);
	}
	AssertStringArrayExpectation(T, Case, TEXT("landscapeLayerNames"), LayerNames);
	AssertLandscapeLayers(T, Case, M);
	AssertMaterialWeightBands(T, Case, M);

	AssertIntExpectation(T, Case, TEXT("foliagePointsPointCount"), M.Foliage.Count);
	AssertBoolExpectation(
		T, Case, TEXT("integrityCheckedFoliagePoints"), !M.Foliage.Sha256.IsEmpty());

	AssertStringArrayExpectation(
		T, Case, TEXT("integrityVerifiedPaths"), FMantlePlaceIntegrityLogic::VerifiablePaths(M));

	{
		TArray<FString> Families, Paths, Pointers, Skipped, Frames;
		for (const FMantlePlaceRecordLayer& Layer : M.PublicRecords.Layers)
		{
			Families.Add(Layer.Family);
			for (const FMantlePlaceRecordFile& File : Layer.Files)
			{
				Paths.Add(File.Path);
				if (File.Frame.bStated)
				{
					Frames.Add(FString::Printf(
						TEXT("%s.%s=%s %s"), *Layer.Family, *File.Kind, *File.Frame.Crs, *File.Frame.HorizontalUnit));
				}
			}
			for (const FMantlePlaceRecordPointer& Pointer : Layer.Pointers)
			{
				Pointers.Add(FString::Printf(TEXT("%s:%s:%s"), *Layer.Family, *Pointer.RecordHolder, *Pointer.Reason));
			}
		}
		for (const FMantlePlaceSkippedSource& Source : M.PublicRecords.SkippedSources)
		{
			Skipped.Add(FString::Printf(TEXT("%s:%s:%s"), *Source.Layer, *Source.RecordHolder, *Source.Reason));
		}
		TArray<FString> Verified;
		for (const FString& Path : FMantlePlaceIntegrityLogic::VerifiablePaths(M))
		{
			if (Paths.Contains(Path))
			{
				Verified.Add(Path);
			}
		}
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordFamilies"), Families);
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordPaths"), Paths);
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordVerifiedPaths"), Verified);
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordPointers"), Pointers);
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordSkippedSources"), Skipped);
		AssertStringArrayExpectation(T, Case, TEXT("publicRecordFrames"), Frames);
	}

	const FMantlePlaceDrapeAlignment Alignment =
		FMantlePlaceDrapeAlignmentLogic::Classify(M.Drape.Alignment);
	AssertStringExpectation(
		T, Case, TEXT("drapeAlignmentShape"), FString(AlignmentShapeName(Alignment.Shape)));
	AssertBoolExpectation(T, Case, TEXT("drapeAlignmentWarns"), Alignment.ShouldWarn());
	AssertTupleExpectation(T, Case, TEXT("drapeAlignmentMeasured"), TEXT("drapeAlignmentMeasuredTolerance"),
		{ Alignment.CoveredPercent, Alignment.OvershootRatio });
}

FString DrivenPayload(const FCase& Case)
{
	if (Case.Id != TEXT("manifest.reject.majorAhead") || !Case.PayloadObject.IsValid())
	{
		return Case.Payload;
	}
	FString Stamped;
	Case.PayloadObject->TryGetStringField(TEXT("version"), Stamped);
	const FMantlePlaceManifestVersion Version = FMantlePlaceManifestVersion::Parse(Stamped);
	if (Version.bValid && Version.Major > MantlePlaceMaxSupportedManifestMajor)
	{
		return Case.Payload;
	}
	const TSharedRef<FJsonObject> Restamped = MakeShared<FJsonObject>();
	Restamped->Values = Case.PayloadObject->Values;
	Restamped->SetStringField(
		TEXT("version"), FString::Printf(TEXT("%d.0.0"), MantlePlaceMaxSupportedManifestMajor + 1));
	FString Out;
	const TSharedRef<TJsonWriter<TCHAR>> Writer = TJsonWriterFactory<TCHAR>::Create(&Out);
	FJsonSerializer::Serialize(Restamped, Writer);
	return Out;
}

bool OverlayMembers(const TSharedPtr<FJsonObject>& Row, const TCHAR* OverlayField, const TSharedPtr<FJsonObject>& Target)
{
	const TSharedPtr<FJsonObject>* Overlay = nullptr;
	if (!Row.IsValid() || !Row->TryGetObjectField(OverlayField, Overlay))
	{
		return true;
	}
	if (!Target.IsValid())
	{
		return false;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Member : (*Overlay)->Values)
	{
		if (!Member.Value.IsValid() || Member.Value->IsNull())
		{
			Target->RemoveField(Member.Key);
		}
		else
		{
			Target->SetField(Member.Key, Member.Value);
		}
	}
	return true;
}

TSharedPtr<FJsonObject> RoadSplinesLayer(const TSharedPtr<FJsonObject>& Manifest)
{
	const TSharedPtr<FJsonObject>* Vector = nullptr;
	if (!Manifest.IsValid() || !Manifest->TryGetObjectField(TEXT("vector"), Vector))
	{
		return nullptr;
	}
	const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
	if (!(*Vector)->TryGetArrayField(TEXT("layers"), Layers))
	{
		return nullptr;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Layers)
	{
		const TSharedPtr<FJsonObject>* Layer = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Layer))
		{
			continue;
		}
		FString Name;
		if ((*Layer)->TryGetStringField(TEXT("name"), Name) && Name == TEXT("road_splines"))
		{
			return *Layer;
		}
	}
	return nullptr;
}

FString ManifestWithOverlays(const FCase& Case, const TSharedPtr<FJsonObject>& Row)
{
	TSharedPtr<FJsonObject> Manifest;
	const TSharedRef<TJsonReader<TCHAR>> Reader =
		TJsonReaderFactory<TCHAR>::Create(RowBodyAsText(Case.PayloadObject, TEXT("manifest")));
	if (!FJsonSerializer::Deserialize(Reader, Manifest) || !Manifest.IsValid())
	{
		return FString();
	}
	const auto Child = [](const TSharedPtr<FJsonObject>& Parent, const TCHAR* Field) {
		const TSharedPtr<FJsonObject>* Found = nullptr;
		return Parent.IsValid() && Parent->TryGetObjectField(Field, Found) ? *Found : TSharedPtr<FJsonObject>();
	};
	const TSharedPtr<FJsonObject> Unreal = Child(Child(Manifest, TEXT("hosts")), TEXT("unreal"));
	if (!OverlayMembers(Row, TEXT("root"), Manifest)
		|| !OverlayMembers(Row, TEXT("unreal"), Unreal)
		|| !OverlayMembers(Row, TEXT("foliagePoints"), Child(Unreal, TEXT("foliage_points")))
		|| !OverlayMembers(Row, TEXT("engineRoadSplines"), Child(Unreal, TEXT("road_splines"))))
	{
		return FString();
	}
	if (!OverlayMembers(Row, TEXT("roadSplines"), RoadSplinesLayer(Manifest)))
	{
		return FString();
	}

	FString Text;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
	FJsonSerializer::Serialize(Manifest.ToSharedRef(), Writer);
	return Text;
}

void AssertTreePointsRow(
	FAutomationTestBase& T,
	const FCase& Case,
	const TSharedPtr<FJsonObject>& Row,
	double OriginEastingM,
	double OriginNorthingM,
	const FMantlePlaceTreePointsFrame& Frame,
	const FVector2D& LandscapeSpanUeCm)
{
	const FString Name = RowString(Row, TEXT("name"));
	const FString ErrorContains = RowString(Row, TEXT("errorContains"));
	const int32 Declared = static_cast<int32>(RowNumber(Row, TEXT("declaredPointCount")));

	TArray<FMantlePlaceTreePointRow> Parsed;
	FString Error;
	const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
		RowString(Row, TEXT("csv")), OriginEastingM, OriginNorthingM, Frame, LandscapeSpanUeCm, Declared, Parsed,
		Error);

	const TCHAR* ActualOutcome = TEXT("?");
	switch (Outcome)
	{
	case EMantlePlaceTreePointsOutcome::Parsed:             ActualOutcome = TEXT("parsed"); break;
	case EMantlePlaceTreePointsOutcome::HeaderUnrecognised: ActualOutcome = TEXT("headerUnrecognised"); break;
	case EMantlePlaceTreePointsOutcome::CountMismatch:      ActualOutcome = TEXT("countMismatch"); break;
	case EMantlePlaceTreePointsOutcome::Unplaceable:        ActualOutcome = TEXT("unplaceable"); break;
	}
	T.TestEqual(
		FString::Printf(TEXT("[%s] \"%s\" outcome (%s)"), *Case.Id, *Name, *Error),
		FString(ActualOutcome), RowString(Row, TEXT("outcome")));

	if (Outcome == EMantlePlaceTreePointsOutcome::Parsed)
	{
		int32 ExpectedRows = 0;
		if (Row.IsValid() && Row->TryGetNumberField(TEXT("parsedRows"), ExpectedRows))
		{
			T.TestEqual(
				FString::Printf(TEXT("[%s] \"%s\" row count"), *Case.Id, *Name),
				Parsed.Num(), ExpectedRows);
		}
		return;
	}

	T.TestFalse(FString::Printf(TEXT("[%s] \"%s\" states a reason"), *Case.Id, *Name), Error.IsEmpty());
	T.TestEqual(
		FString::Printf(TEXT("[%s] \"%s\" discards the rows it will not stand behind"), *Case.Id, *Name),
		Parsed.Num(), 0);
	if (!ErrorContains.IsEmpty())
	{
		T.TestTrue(
			FString::Printf(TEXT("[%s] \"%s\" message contains \"%s\" (got: %s)"),
				*Case.Id, *Name, *ErrorContains, *Error),
			Error.Contains(ErrorContains));
	}
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportManifestTest,
	"MantlePlace.Import.Manifest",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportManifestTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("manifest"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}

	TestTrue(
		FString::Printf(
			TEXT("corpus manifestVersion (%s) >= this host's MinSupportedManifestVersion (%s) "
			     "(clean break, HPS-31; independent repin)"),
			*PinnedManifestVersion(),
			*MantlePlaceMinSupportedManifestVersion),
		!MantlePlaceIsManifestVersionBelowFloor(PinnedManifestVersion(), MantlePlaceMinSupportedManifestVersion));

	TSet<FString> Driven;
	for (const FCase& Case : Cases)
	{
		if (Case.IsVector())
		{
			continue;
		}
		Driven.Add(Case.Id);

		FString Error;
		const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(DrivenPayload(Case), Error);

		if (Case.IsAccept())
		{
			TestTrue(Case.What(TEXT("accepted")), M.bValid);
			if (!M.bValid)
			{
				AddError(FString::Printf(TEXT("[%s] rejected with: %s"), *Case.Id, *Error));
			}
			TestEqual(Case.What(TEXT("accepted with no error text")), Error, FString());
		}
		else if (Case.IsReject())
		{
			TestFalse(Case.What(TEXT("rejected")), M.bValid);
			TestFalse(Case.What(TEXT("rejection states a reason")), Error.IsEmpty());
		}
		else
		{
			AddError(FString::Printf(TEXT("[%s] unsupported expect '%s' in the manifest group"),
				*Case.Id, *Case.Expect));
			continue;
		}

		if (!Case.ErrorContains.IsEmpty())
		{
			TestTrue(
				FString::Printf(TEXT("[%s] message contains \"%s\" (got: %s)"),
					*Case.Id, *Case.ErrorContains, *Error),
				Error.Contains(Case.ErrorContains));
		}

		AssertExpectations(*this, Case, M);

		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

		for (const FString& Problem : UnassertedNestedExpectations(Case))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.materializationSignals")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const TSharedPtr<FJsonObject>* Manifest = nullptr;
			if (!Row.IsValid() || !Row->TryGetObjectField(TEXT("manifest"), Manifest) || Manifest == nullptr)
			{
				AddError(FString::Printf(TEXT("[%s] row \"%s\" has no embedded manifest"), *Case->Id, *Name));
				continue;
			}
			const bool bExpected = RowBool(Row, TEXT("materialized"));
			TestTrue(
				FString::Printf(TEXT("[%s] \"%s\" -> materialized == %s"),
					*Case->Id, *Name, bExpected ? TEXT("true") : TEXT("false")),
				MantlePlaceImportManifest::IsBundleMaterialized(*Manifest) == bExpected);
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.materializationSignals has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.treePointsRowCount")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			AssertTreePointsRow(*this, *Case, Row, 441959.5, 4014372.5, FMantlePlaceTreePointsFrame(),
				FVector2D(140100.0, 142500.0));
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.treePointsRowCount has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.treePointsFrame")))
	{
		Driven.Add(Case->Id);
		FString ManifestError;
		const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(
			RowBodyAsText(Case->PayloadObject, TEXT("manifest")), ManifestError);
		TestTrue(Case->What(*FString::Printf(TEXT("embedded manifest parses (%s)"), *ManifestError)), M.bValid);
		const FMantlePlaceTreePointsFrame Frame = M.GetFoliagePointsFrame();
		TestFalse(Case->What(TEXT("a pre-1.3.0 pointer that states no frame owes none")), Frame.IsOwed());

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			AssertTreePointsRow(*this, *Case, Row, M.Georef.OriginEastingM, M.Georef.OriginNorthingM, Frame, M.GetAoiSizeUeCm());
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.treePointsFrame has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.treePointsStatedFrame")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			FString ManifestError;
			const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(
				ManifestWithOverlays(*Case, Row), ManifestError);
			TestTrue(Case->What(*FString::Printf(TEXT("\"%s\": embedded manifest parses (%s)"),
				*RowString(Row, TEXT("name")), *ManifestError)), M.bValid);
			AssertTreePointsRow(*this, *Case, Row, M.Georef.OriginEastingM, M.Georef.OriginNorthingM,
				M.GetFoliagePointsFrame(), M.GetAoiSizeUeCm());
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.treePointsStatedFrame has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.foliageTypeUnknownValue")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const FString Vocabulary = RowString(Row, TEXT("foliageTypeVocabulary"));
			TArray<FMantlePlaceTreePointRow> Parsed;
			FString Error;
			const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
				RowString(Row, TEXT("csv")), 441959.5, 4014372.5, FMantlePlaceTreePointsFrame(),
				FVector2D(140100.0, 142500.0), 0, Parsed, Error, Vocabulary);
			TestTrue(Case->What(*FString::Printf(TEXT("\"%s\": parses (%s)"), *Name, *Error)),
				Outcome == EMantlePlaceTreePointsOutcome::Parsed);

			const TArray<FString> Expected = RowStrings(Row, TEXT("foliageTypes"));
			TestEqual(Case->What(*FString::Printf(TEXT("\"%s\": no point is dropped for its type"), *Name)),
				Parsed.Num(), Expected.Num());
			for (int32 Index = 0; Index < FMath::Min(Parsed.Num(), Expected.Num()); ++Index)
			{
				const TCHAR* Actual =
					Parsed[Index].FoliageType == EMantlePlaceFoliageType::Shrub ? TEXT("shrub") : TEXT("tree");
				TestEqual(Case->What(*FString::Printf(TEXT("\"%s\": row %d type"), *Name, Index)),
					FString(Actual), Expected[Index]);
			}
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.foliageTypeUnknownValue has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.roadSplinesStatedDatum")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const FString Overlaid = ManifestWithOverlays(*Case, Row);
			FString ManifestError;
			const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(Overlaid, ManifestError);
			const FString Parses = FString::Printf(TEXT("\"%s\": embedded manifest parses (%s)"), *Name, *ManifestError);
			TestTrue(Case->What(*Parses), M.bValid);
			const FString Selected = FString::Printf(TEXT("\"%s\": the road layer is selected"), *Name);
			TestTrue(Case->What(*Selected), M.RoadSplines.bPresent);

			FString Reason;
			const bool bPlaceable = FMantlePlaceRoadSplinesLogic::CanPlaceHeights(M.GetRoadSplinesDatum(), Reason);
			const FString Outcome = FString::Printf(TEXT("\"%s\" outcome (%s)"), *Name, *Reason);
			const FString ActualOutcome = bPlaceable ? TEXT("placeable") : TEXT("unplaceable");
			TestEqual(Case->What(*Outcome), ActualOutcome, RowString(Row, TEXT("outcome")));
			if (bPlaceable)
			{
				const FString NoRefusal = FString::Printf(TEXT("\"%s\": a placeable layer carries no refusal"), *Name);
				TestTrue(Case->What(*NoRefusal), Reason.IsEmpty());
				continue;
			}
			const FString StatesReason = FString::Printf(TEXT("\"%s\" states a reason"), *Name);
			TestFalse(Case->What(*StatesReason), Reason.IsEmpty());
			const FString ErrorContains = RowString(Row, TEXT("errorContains"));
			if (!ErrorContains.IsEmpty())
			{
				const FString Contains = FString::Printf(TEXT("\"%s\" message contains \"%s\" (got: %s)"), *Name, *ErrorContains, *Reason);
				TestTrue(Case->What(*Contains), Reason.Contains(ErrorContains, ESearchCase::CaseSensitive));
			}
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.roadSplinesStatedDatum has gone missing"));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("manifest.unrealRoadSplinesPointer")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has rows")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const FString Overlaid = ManifestWithOverlays(*Case, Row);
			FString ManifestError;
			const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(Overlaid, ManifestError);
			const FString Parses = FString::Printf(TEXT("\"%s\": embedded manifest parses (%s)"), *Name, *ManifestError);
			TestTrue(Case->What(*Parses), M.bValid);

			TArray<FString> Declared;
			TArray<FString> Placeable;
			TArray<FString> Refusals;
			for (const FMantlePlaceRoadSplinesCandidate& Candidate : M.GetRoadSplinesPlan())
			{
				Declared.Add(FMantlePlaceRoadSplinesLogic::SourceName(Candidate.Source));
				if (Candidate.bPlaceable)
				{
					Placeable.Add(FMantlePlaceRoadSplinesLogic::SourceName(Candidate.Source));
					const FString NoRefusal = FString::Printf(TEXT("\"%s\": a placeable source carries no refusal"), *Name);
					TestTrue(Case->What(*NoRefusal), Candidate.Refusal.IsEmpty());
				}
				else
				{
					const FString StatesReason = FString::Printf(TEXT("\"%s\": a refused source states a reason"), *Name);
					TestFalse(Case->What(*StatesReason), Candidate.Refusal.IsEmpty());
					Refusals.Add(Candidate.Refusal);
				}
			}
			const FString DeclaredWhat = FString::Printf(TEXT("\"%s\": the sources the manifest declares, in order"), *Name);
			TestEqual(Case->What(*DeclaredWhat), FString::Join(Declared, TEXT(",")),
				FString::Join(RowStrings(Row, TEXT("declared")), TEXT(",")));
			const FString PlaceableWhat = FString::Printf(TEXT("\"%s\": the sources whose heights are placed, in order"), *Name);
			TestEqual(Case->What(*PlaceableWhat), FString::Join(Placeable, TEXT(",")),
				FString::Join(RowStrings(Row, TEXT("placeable")), TEXT(",")));

			const FString EnginePath = RowString(Row, TEXT("enginePath"));
			if (!EnginePath.IsEmpty())
			{
				const FString PathWhat = FString::Printf(TEXT("\"%s\": the copy is read from its own pointer"), *Name);
				TestEqual(Case->What(*PathWhat), M.RoadSplines.EnginePath, EnginePath);
			}
			const FString ErrorContains = RowString(Row, TEXT("errorContains"));
			if (!ErrorContains.IsEmpty())
			{
				const FString All = FString::Join(Refusals, TEXT(" "));
				const FString Contains = FString::Printf(TEXT("\"%s\" a refusal contains \"%s\" (got: %s)"), *Name, *ErrorContains, *All);
				TestTrue(Case->What(*Contains), All.Contains(ErrorContains, ESearchCase::CaseSensitive));
			}
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.unrealRoadSplinesPointer has gone missing"));
	}

	for (const FCase& Case : Cases)
	{
		if (!Case.IsVector())
		{
			continue;
		}
		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

		for (const FString& Problem : UnassertedNestedExpectations(Case))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the manifest group but nothing in this suite drives it. ")
			TEXT("A host consumes every case in the groups it claims (HPS-41)."),
			*Missing));
	}

	{
		TestEqual(TEXT("empty path yields empty prefix"),
			MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(FString()), FString());
		TestEqual(TEXT("bare filename (no dir) yields empty prefix, not \"/\""),
			MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(TEXT("layer.json")), FString());

		TestEqual(TEXT("epsg from string"), MantlePlaceImportManifest::ParseEpsg(TEXT("EPSG:32613")), 32613);
		TestEqual(TEXT("epsg from bare"), MantlePlaceImportManifest::ParseEpsg(TEXT("32618")), 32618);
		TestEqual(TEXT("epsg empty"), MantlePlaceImportManifest::ParseEpsg(TEXT("")), 0);
	}

	{
		const FVector North = FMantlePlaceVaultManifest::ProjectedToUeCm(0.0, 100.0, 0.0);
		TestEqual(TEXT("100 m north -> +10000 cm X"), North.X, 10000.0, 1e-9);
		TestEqual(TEXT("100 m north -> 0 Y"), North.Y, 0.0, 1e-9);

		const FVector East = FMantlePlaceVaultManifest::ProjectedToUeCm(100.0, 0.0, 0.0);
		TestEqual(TEXT("100 m east -> +10000 cm Y"), East.Y, 10000.0, 1e-9);
		TestEqual(TEXT("100 m east -> 0 X"), East.X, 0.0, 1e-9);

		const FVector Up = FMantlePlaceVaultManifest::ProjectedToUeCm(0.0, 0.0, 100.0);
		TestEqual(TEXT("100 m up -> +10000 cm Z"), Up.Z, 10000.0, 1e-9);

		const FRotator MeshRot = FMantlePlaceVaultManifest::GetMeshRotation();
		const FVector MeshEast = MeshRot.RotateVector(FVector(1.0, 0.0, 0.0));
		const FVector MeshSouth = MeshRot.RotateVector(FVector(0.0, 1.0, 0.0));
		TestEqual(TEXT("mesh +X (east) -> world +Y"), MeshEast.Y, 1.0, 1e-6);
		TestEqual(TEXT("mesh +X (east) -> no world X"), MeshEast.X, 0.0, 1e-6);
		TestEqual(TEXT("mesh +Y (south) -> world -X"), MeshSouth.X, -1.0, 1e-6);
		TestEqual(TEXT("mesh +Y (south) -> no world Y"), MeshSouth.Y, 0.0, 1e-6);
		TestEqual(TEXT("mesh correction is a rotation, not a mirror (det +1)"),
			FVector::CrossProduct(MeshEast, MeshSouth).Z, FVector::CrossProduct(
				FVector(1.0, 0.0, 0.0), FVector(0.0, 1.0, 0.0)).Z, 1e-6);
	}

	{
		FString Error;
		const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.0.0\",")
		        TEXT("\"layout\":{\"cesium_terrain\":\"Elevation/Terrain/layer.json\"},")
		            TEXT("\"cesium_terrain\":{\"present\":true,\"tile_count\":1287},")
		                TEXT("\"terrain\":{\"cesiumTerrainTileCount\":42},")
		                    TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestEqual(TEXT("tile count"), M.Cesium.TerrainTileCount, 1287);
		TestTrue(TEXT("bundle has cesium terrain"), M.Cesium.bHasTerrain);

		Error.Reset();
		const FMantlePlaceVaultManifest Legacy = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.0.0\",")
		        TEXT("\"layout\":{\"cesium_terrain\":\"Elevation/Terrain/layer.json\"},")
		            TEXT("\"terrain\":{\"cesiumTerrainTileCount\":42},")
		                TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestEqual(TEXT("retired pre-v13 tile-count key is ignored"), Legacy.Cesium.TerrainTileCount, 0);
	}

	{
		FString Error;
		const FMantlePlaceVaultManifest Integer = MantlePlaceImportManifest::Parse(
		    TEXT("{\"jobId\":\"x\",\"version\":19,")
		        TEXT("\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}"),
		    Error);
		TestFalse(TEXT("an integer-era manifest is refused"), Integer.bValid);
		TestTrue(TEXT("refusal names it as no longer supported"), Error.Contains(TEXT("no longer supported")));

		Error.Reset();
		const FMantlePlaceVaultManifest Absent = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\"}"), Error);
		TestFalse(TEXT("an absent version is refused"), Absent.bValid);
		TestTrue(TEXT("absent version is named, not shown as 0"), Error.Contains(TEXT("(absent)")));

		Error.Reset();
		const FMantlePlaceVaultManifest Second = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"2.0.0\",")
		        TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Site/Terrain.glb\"}}}}"),
		    Error);
		TestTrue(TEXT("a 2.0 manifest is read"), Second.bValid);
		TestEqual(TEXT("a 2.0 manifest is read through its pointer"), Second.Mesh.Path, FString(TEXT("Site/Terrain.glb")));
		TestEqual(TEXT("a 2.0 manifest is read with no error text"), Error, FString());

		Error.Reset();
		const FString AheadVersion = FString::Printf(TEXT("%d.0.0"), MantlePlaceMaxSupportedManifestMajor + 1);
		const FMantlePlaceVaultManifest Future = MantlePlaceImportManifest::Parse(
		    FString::Printf(
		        TEXT("{\"job_id\":\"x\",\"version\":\"%s\",")
		        TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Site/Terrain.glb\"}}}}"),
		        *AheadVersion),
		    Error);
		TestFalse(TEXT("an unknown higher MAJOR is refused"), Future.bValid);
		TestTrue(TEXT("the refusal says to update the plugin, not to re-download"),
			Error.Contains(TEXT("Update the Mantle Place plugin")));
		TestTrue(TEXT("the refusal names the version it saw"), Error.Contains(AheadVersion));

		Error.Reset();
		const FMantlePlaceVaultManifest AdditiveMinor = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.7.3\",\"an_unknown_future_key\":42,")
		        TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestTrue(TEXT("a higher MINOR is accepted — minors are strictly additive"), AdditiveMinor.bValid);
	}

	{
		const FCase* BaseCase = FindCase(Cases, TEXT("manifest.baseOnDemand"));
		if (BaseCase == nullptr)
		{
			AddError(TEXT("corpus case manifest.baseOnDemand has gone missing"));
		}
		else
		{
			FString Error;
			MantlePlaceImportManifest::Parse(BaseCase->Payload, Error);
			TestTrue(TEXT("guidance mentions the vault"), Error.Contains(TEXT("mantle.place/vault")));
			TestTrue(TEXT("guidance offers Stream into Cesium as an interim preview"),
			         Error.Contains(TEXT("Stream into Cesium")));
		}
	}

	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceConformanceReaderSelfTest,
	"MantlePlace.Conformance.ReaderSelfTest",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceConformanceReaderSelfTest::RunTest(const FString& Parameters)
{
	TestTrue(TEXT("no corpus, source tree, CI unset: the corpus tests skip"), MissingCorpusSkips(FString(), false));
	TestFalse(TEXT("no corpus, source tree, CI=true: the corpus tests fail"), MissingCorpusSkips(TEXT("true"), false));
	TestFalse(TEXT("no corpus, source tree, CI=1: the corpus tests fail"), MissingCorpusSkips(TEXT("1"), false));
	TestTrue(TEXT("no corpus, packaged, CI=true: the corpus tests skip"), MissingCorpusSkips(TEXT("true"), true));
	TestTrue(TEXT("no corpus, packaged, CI unset: the corpus tests skip"), MissingCorpusSkips(FString(), true));

	if (SkipWithoutCorpus(*this))
	{
		return true;
	}

	const FString CorpusRoot = FindCorpusRoot();
	if (CorpusRoot.IsEmpty())
	{
		AddError(TEXT("could not locate tools/manifest-conformance/corpus/ — without it the reader ")
		         TEXT("self-test (HPS-46) cannot run, and in CI it must fail rather than skip"));
		return false;
	}
	const FString SelfTestDir = FPaths::Combine(CorpusRoot, TEXT("self-test"));

	TArray<FCase> Cases;
	FString Error;
	TestFalse(TEXT("a rotten index does not load cleanly"),
		LoadGroupFromDir(SelfTestDir, TEXT("manifest"), Cases, Error));
	TestTrue(TEXT("missing-file fixture is flagged (selfTestFailure: missingFile)"),
		Error.Contains(TEXT("selfTest.missingFile")) && Error.Contains(TEXT("missing vector file")));
	TestTrue(TEXT("undeclared-malformed fixture is flagged (selfTestFailure: malformedCase)"),
		Error.Contains(TEXT("selfTest.malformedCase")) && Error.Contains(TEXT("malformedJson")));
	TestTrue(TEXT("duplicate-id fixture is flagged (selfTestFailure: duplicateId)"),
		Error.Contains(TEXT("selfTest.duplicateId")) && Error.Contains(TEXT("more than once")));

	static const TCHAR* const SelfTestConsumedKeys[] = { TEXT("orderId") };

	if (const FCase* Unknown = FindCase(Cases, TEXT("selfTest.unknownExpectationKey")))
	{
		FString OrderId;
		TestTrue(Unknown->What(TEXT("orderId reads as the consumed key it is")),
			WantsString(*Unknown, TEXT("orderId"), OrderId));
		const TArray<FString> Problems = UnassertedExpectations(*Unknown, SelfTestConsumedKeys);
		TestEqual(Unknown->What(TEXT("exactly one unasserted key")), Problems.Num(), 1);
		TestTrue(Unknown->What(TEXT("the unknown key fails as \"does not assert\"")),
			Problems.Num() == 1
				&& Problems[0].Contains(TEXT("selfTestNeverConsumed"))
				&& Problems[0].Contains(TEXT("does not assert")));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.unknownExpectationKey did not load"));
	}

	if (const FCase* WrongType = FindCase(Cases, TEXT("selfTest.wrongTypeExpectation")))
	{
		FString OrderId;
		TestFalse(WrongType->What(TEXT("a number does not read as a string")),
			WantsString(*WrongType, TEXT("orderId"), OrderId));
		const TArray<FString> Problems = UnassertedExpectations(*WrongType, SelfTestConsumedKeys);
		TestEqual(WrongType->What(TEXT("exactly one unasserted key")), Problems.Num(), 1);
		TestTrue(WrongType->What(TEXT("the mistyped key fails as \"unexpected JSON type\"")),
			Problems.Num() == 1
				&& Problems[0].Contains(TEXT("orderId"))
				&& Problems[0].Contains(TEXT("unexpected JSON type")));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.wrongTypeExpectation did not load"));
	}

	if (const FCase* Nested = FindCase(Cases, TEXT("selfTest.nestedUnreadExpectation")))
	{
		FString OrderId;
		TestTrue(Nested->What(TEXT("the top-level orderId reads")),
			WantsString(*Nested, TEXT("orderId"), OrderId));

		TArray<TSharedPtr<FJsonObject>> Rows;
		TestTrue(Nested->What(TEXT("the top-level items key reads")),
			WantsObjectRows(*Nested, TEXT("items"), Rows));
		TestEqual(Nested->What(TEXT("the fixture has one row")), Rows.Num(), 1);

		if (Rows.Num() == 1)
		{
			const FString Path = TEXT("items[0]");
			FString Text;
			TestTrue(Nested->What(TEXT("items[0].orderId reads")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("orderId"), Text));

			TestFalse(Nested->What(TEXT("items[0].sha256 is null, so there is no value to assert")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("sha256"), Text));

			TestFalse(Nested->What(TEXT("a strictly typed read gets nothing from a number")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("status"), Text));
		}

		TestEqual(Nested->What(TEXT("HPS-46 alone reports this fixture covered")),
			UnassertedExpectations(*Nested, SelfTestConsumedKeys).Num(), 0);

		const TArray<FString> Unread = UnassertedNestedExpectations(*Nested);
		TestEqual(Nested->What(TEXT("both unread nested keys are flagged")), Unread.Num(), 2);
		TestTrue(Nested->What(TEXT("the key nothing asserts is named, with its path")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].selfTestNeverReadNested"));
			}));
		TestTrue(Nested->What(TEXT("the wrong-typed key fails identically, not by coercion")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].status"));
			}));
		TestFalse(Nested->What(TEXT("prose is exempt at depth, not only at the top level")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("selfTestNote"));
			}));
		TestFalse(Nested->What(TEXT("and an explicit null that WAS read is not reported")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].sha256"));
			}));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.nestedUnreadExpectation did not load"));
	}

	{
		TArray<FString> Orphans;
		FString SweepError;
		TestTrue(TEXT("the directory sweep runs"), UnindexedCaseFiles(SelfTestDir, Orphans, SweepError));
		TestTrue(TEXT("the orphan fixture is flagged (index orphanFiles: cases/orphan.json)"),
			Orphans.Contains(TEXT("cases/orphan.json")));
		TestEqual(TEXT("nothing else is flagged (broken-index-*/ are nested corpora, not orphans)"),
			Orphans.Num(), 1);
	}

	for (const TCHAR* Broken : { TEXT("broken-index-json"), TEXT("broken-index-schema") })
	{
		TArray<FCase> BrokenCases;
		FString BrokenError;
		TestFalse(FString::Printf(TEXT("%s fails to load"), Broken),
			LoadGroupFromDir(FPaths::Combine(SelfTestDir, Broken), TEXT("manifest"), BrokenCases, BrokenError));
		TestTrue(FString::Printf(TEXT("%s failure states a reason and yields no cases"), Broken),
			BrokenCases.Num() == 0 && !BrokenError.IsEmpty());
	}

	return true;
}

#endif
