// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceLiveGateLogic.h"
#include "MantlePlaceRoadSplinesLogic.h"
#include "MantlePlaceRoadSpline.h"
#include "MantlePlaceCoverageRasterLogic.h"
#include "MantlePlaceCoverageRasterTypes.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Editor/Transactor.h"
#include "Engine/Texture2D.h"
#include "HAL/PlatformMisc.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "HAL/PlatformFileManager.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Landscape.h"
#include "LandscapeComponent.h"
#include "LandscapeDataAccess.h"
#include "LandscapeInfo.h"
#include "LandscapeLayerInfoObject.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstance.h"
#include "Dom/JsonObject.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace MantlePlaceImportLiveTest
{
	double Correlation(const TArray<double>& A, const TArray<double>& B)
	{
		const int32 Num = FMath::Min(A.Num(), B.Num());
		if (Num < 3)
		{
			return 0.0;
		}
		double MeanA = 0.0;
		double MeanB = 0.0;
		for (int32 Index = 0; Index < Num; ++Index)
		{
			MeanA += A[Index];
			MeanB += B[Index];
		}
		MeanA /= Num;
		MeanB /= Num;

		double Sab = 0.0;
		double Saa = 0.0;
		double Sbb = 0.0;
		for (int32 Index = 0; Index < Num; ++Index)
		{
			const double Da = A[Index] - MeanA;
			const double Db = B[Index] - MeanB;
			Sab += Da * Db;
			Saa += Da * Da;
			Sbb += Db * Db;
		}
		if (Saa <= 0.0 || Sbb <= 0.0)
		{
			return 0.0;
		}
		return Sab / FMath::Sqrt(Saa * Sbb);
	}

	enum class EDihedral : uint8
	{
		Identity = 0, Rot90CCW, Rot180, Rot270CCW, Transpose, FlipNorthSouth, FlipEastWest, AntiTranspose, Count
	};

	const TCHAR* DihedralName(EDihedral Which)
	{
		switch (Which)
		{
		case EDihedral::Identity:       return TEXT("identity");
		case EDihedral::Rot90CCW:       return TEXT("rot90 CCW");
		case EDihedral::Rot180:         return TEXT("rot180");
		case EDihedral::Rot270CCW:      return TEXT("rot270 CCW");
		case EDihedral::Transpose:      return TEXT("transpose");
		case EDihedral::FlipNorthSouth: return TEXT("flip N/S");
		case EDihedral::FlipEastWest:   return TEXT("flip E/W");
		default:                        return TEXT("anti-transpose");
		}
	}

	TArray<double> TransformGrid(const TArray<double>& In, int32 N, EDihedral Which)
	{
		TArray<double> Out;
		Out.SetNumUninitialized(N * N);
		for (int32 I = 0; I < N; ++I)
		{
			for (int32 J = 0; J < N; ++J)
			{
				int32 SrcI = I;
				int32 SrcJ = J;
				switch (Which)
				{
				case EDihedral::Identity:       SrcI = I;         SrcJ = J;         break;
				case EDihedral::Rot90CCW:       SrcI = J;         SrcJ = N - 1 - I; break;
				case EDihedral::Rot180:         SrcI = N - 1 - I; SrcJ = N - 1 - J; break;
				case EDihedral::Rot270CCW:      SrcI = N - 1 - J; SrcJ = I;         break;
				case EDihedral::Transpose:      SrcI = J;         SrcJ = I;         break;
				case EDihedral::FlipNorthSouth: SrcI = N - 1 - I; SrcJ = J;         break;
				case EDihedral::FlipEastWest:   SrcI = I;         SrcJ = N - 1 - J; break;
				default:                        SrcI = N - 1 - J; SrcJ = N - 1 - I; break;
				}
				Out[I * N + J] = In[SrcI * N + SrcJ];
			}
		}
		return Out;
	}

	int32 CellCentre(int32 Index, int32 Count, int32 Extent)
	{
		return FMath::Clamp(FMath::RoundToInt32((Index + 0.5) * Extent / Count), 0, Extent - 1);
	}

	TArray<ULandscapeComponent*> GatherLandscapeComponents(ALandscape* Landscape)
	{
		TArray<ULandscapeComponent*> Components;
		if (Landscape == nullptr)
		{
			return Components;
		}
		if (ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			Info->ForAllLandscapeComponents([&Components](ULandscapeComponent* Component)
				{
					Components.Add(Component);
				});
		}
		return Components;
	}

	bool IsCi()
	{
		return !FPlatformMisc::GetEnvironmentVariable(TEXT("CI")).IsEmpty();
	}

	struct FCheckPoint
	{
		double XCm = 0.0;
		double YCm = 0.0;
		double ExpectedZCm = 0.0;
	};

	const TCHAR* const LandscapeDatum = TEXT("EGM2008-orthometric");

	bool ReadCheckPoints(FZipArchiveReader& Reader, int32 Wanted, TArray<FCheckPoint>& Out, FString& OutReason,
		bool& bOutNotStatedBy3x)
	{
		Out.Reset();
		bOutNotStatedBy3x = false;
		{
			FString Root, FoundPath;
			if (FMantlePlaceBundleReaderLogic::FindManifest(Reader.GetFileNames(), Root, FoundPath)
				&& FoundPath.Equals(FMantlePlaceBundleReaderLogic::ManifestPath3(), ESearchCase::IgnoreCase))
			{
				bOutNotStatedBy3x = true;
				OutReason = TEXT("a 3.x manifest states no surface points file or datum difference to check the "
					"landscape against, and this gate derives none");
				return false;
			}
		}
		TArray<uint8> ManifestBytes;
		if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
		{
			OutReason = TEXT("the bundle has no Metadata/manifest.json");
			return false;
		}
		const FString ManifestText(
			FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(ManifestBytes.GetData()), ManifestBytes.Num()));
		TSharedPtr<FJsonObject> Root;
		if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(ManifestText), Root) || !Root.IsValid())
		{
			OutReason = TEXT("the manifest does not parse");
			return false;
		}
		const TSharedPtr<FJsonObject>* Elevation = nullptr;
		const TSharedPtr<FJsonObject>* Points = nullptr;
		if (!Root->TryGetObjectField(TEXT("elevation"), Elevation)
			|| !(*Elevation)->TryGetObjectField(TEXT("points_csv"), Points))
		{
			OutReason = TEXT("the bundle publishes no elevation.points_csv");
			return false;
		}
		const FString Path = (*Points)->GetStringField(TEXT("path"));
		const FString Frame = (*Points)->GetStringField(TEXT("horizontal_frame"));
		const FString Datum = (*Points)->GetStringField(TEXT("vertical_datum"));
		if (!Frame.StartsWith(TEXT("local-ENU")))
		{
			OutReason = FString::Printf(TEXT("its surface points are in \"%s\", not local ENU"), *Frame);
			return false;
		}
		FString Units;
		(*Points)->TryGetStringField(TEXT("units"), Units);
		double MetresPerUnit = 1.0;
		if (!FMantlePlaceLiveGateLogic::TryUnitToMetres(Units, MetresPerUnit, OutReason))
		{
			return false;
		}

		double DatumShiftM = 0.0;
		if (!Datum.Equals(LandscapeDatum, ESearchCase::CaseSensitive))
		{
			const TSharedPtr<FJsonObject>* Dem = nullptr;
			double NavdMinusEgm = 0.0;
			if (!Datum.StartsWith(TEXT("NAVD88")) || !(*Elevation)->TryGetObjectField(TEXT("dem"), Dem)
				|| (*Dem)->GetStringField(TEXT("vertical_datum")) != Datum
				|| !(*Dem)->TryGetNumberField(TEXT("navd88_minus_egm2008_m"), NavdMinusEgm))
			{
				OutReason = FString::Printf(
					TEXT("its surface points are in \"%s\" and the bundle publishes no difference to %s"),
					*Datum, LandscapeDatum);
				return false;
			}
			DatumShiftM = -NavdMinusEgm;
		}

		TArray<uint8> CsvBytes;
		if (!Reader.TryReadFile(Path, CsvBytes))
		{
			OutReason = FString::Printf(TEXT("the bundle has no entry \"%s\""), *Path);
			return false;
		}
		const FString CsvText(FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(CsvBytes.GetData()), CsvBytes.Num()));
		TArray<FString> Lines;
		CsvText.ParseIntoArrayLines(Lines);
		const int32 Stride = FMath::Max(1, Lines.Num() / FMath::Max(1, Wanted));
		for (int32 Index = Stride / 2; Index < Lines.Num() && Out.Num() < Wanted; Index += Stride)
		{
			TArray<FString> Cells;
			Lines[Index].ParseIntoArray(Cells, TEXT(","));
			if (Cells.Num() < 3)
			{
				continue;
			}
			FCheckPoint Point;
			const double ToCm = MetresPerUnit * 100.0;
			Point.YCm = FCString::Atod(*Cells[0]) * ToCm;
			Point.XCm = FCString::Atod(*Cells[1]) * ToCm;
			Point.ExpectedZCm = (FCString::Atod(*Cells[2]) * MetresPerUnit + DatumShiftM) * 100.0;
			Out.Add(Point);
		}
		if (Out.IsEmpty())
		{
			OutReason = FString::Printf(TEXT("\"%s\" holds no x,y,z rows"), *Path);
			return false;
		}
		return true;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportLiveTest,
	"MantlePlace.Import.LiveFreeTier",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportLiveTest::RunTest(const FString& Parameters)
{
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();

	const bool bStrict = MantlePlaceImportLiveTest::IsCi();
	auto SkipOrFail = [this, bStrict](const FString& Reason) -> bool
	{
		if (bStrict)
		{
			AddError(FString::Printf(
				TEXT("%s CI is set, so this is a failure rather than a skip: the orientation gate "
				     "must not be able to go green without running. Point MP_LIVETEST_BUNDLE at a "
				     "bundle at or above the current manifest floor."),
				*Reason));
			return false;
		}
		AddWarning(Reason);
		return true;
	};
	FString Zip = FPlatformMisc::GetEnvironmentVariable(TEXT("MP_LIVETEST_BUNDLE"));
	if (Zip.IsEmpty() || !Platform.FileExists(*Zip))
	{
		Zip = FPaths::ProjectSavedDir() / TEXT("MantlePlace/livetest/download.zip");
	}
	if (!Platform.FileExists(*Zip))
	{
		return SkipOrFail(
			FString::Printf(TEXT("Sample bundle not present (%s); skipping live import test."), *Zip));
	}

	FMantlePlaceVaultManifest Manifest;
	{
		FString ReadError;
		const bool bRead = UMantlePlaceImporterLibrary::ReadVaultManifest(Zip, Manifest, ReadError);

		if (bRead && !Manifest.bValid
		    && MantlePlaceIsManifestVersionBelowFloor(Manifest.Version, MantlePlaceMinSupportedManifestVersion))
		{
			return SkipOrFail(FString::Printf(
			    TEXT("Sample bundle is manifest %s, below the %s floor; skipping live import test. "
			         "Re-download this AOI from mantle.place/vault to re-cut it on the current pipeline."),
			    *MantlePlaceDescribeManifestVersion(Manifest.Version), *MantlePlaceMinSupportedManifestVersion));
		}

		if (bRead && !Manifest.bValid && Manifest.DeliveryModel == TEXT("base_on_demand"))
		{
			return SkipOrFail(
			    TEXT("Base bundle — materialize incomplete, live import not exercised. "
			         "Generate its Unreal formats (vault panel or mantle.place/vault) and re-download."));
		}
	}

	if (Manifest.bValid)
	{
		const FString ExpectedRefusal = MantlePlaceImportOrchestration::ExplainNothingToImport(
			MantlePlaceArtifacts::MakeAll(), Manifest, FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Both));
		if (!ExpectedRefusal.IsEmpty())
		{
			UWorld* RefusalWorld = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
			const auto CountActors = [RefusalWorld]()
			{
				int32 Count = 0;
				if (RefusalWorld != nullptr)
				{
					for (TActorIterator<AActor> It(RefusalWorld); It; ++It)
					{
						++Count;
					}
				}
				return Count;
			};
			FAssetRegistryModule& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry"));
			const FName ContentRoot(*UMantlePlaceEditorSettings::ResolveGeneratedContentRoot());
			const auto CountAssets = [&Registry, ContentRoot]()
			{
				TArray<FAssetData> Assets;
				Registry.Get().GetAssetsByPath(ContentRoot, Assets, true);
				return Assets.Num();
			};
			const int32 ActorsBefore = CountActors();
			const int32 AssetsBefore = CountAssets();

			const FMantlePlaceImportResult Refused =
				UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);

			AddInfo(FString::Printf(
				TEXT("Refusal path checked (this archive holds no terrain the plugin can place): jobId=%s\n%s"),
				*Refused.JobId, *Refused.Message));
			TestFalse(TEXT("an archive with no placeable terrain is refused, not imported"), Refused.bSuccess);
			TestTrue(TEXT("the refusal says there is nothing to import"),
				Refused.Message.StartsWith(TEXT("Nothing to import: ")));
			TestEqual(TEXT("the refusal is the importer's own explanation of the archive"), Refused.Message, ExpectedRefusal);
			TestTrue(TEXT("the refusal promises the existing assets are untouched"),
				Refused.Message.Contains(TEXT("Existing assets left untouched")));
			TestEqual(TEXT("the Cesium hint is made exactly when the archive holds a Cesium tileset"),
				Refused.Message.Contains(TEXT("Cesium quantized-mesh terrain")),
				FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(Manifest));
			TestEqual(TEXT("the refusal created no actors"), Refused.CreatedActors.Num(), 0);
			TestEqual(TEXT("and left the level's actors as they were"), CountActors(), ActorsBefore);
			TestEqual(TEXT("and left the generated content as it was"), CountAssets(), AssetsBefore);
			return true;
		}
	}

	const FMantlePlaceImportResult Result =
		UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);

	AddInfo(FString::Printf(TEXT("jobId=%s actors=%d\n%s"),
		*Result.JobId, Result.CreatedActors.Num(), *Result.Message));

	TestTrue(TEXT("import reported success"), Result.bSuccess);
	TestTrue(TEXT("created at least 2 actors (landscape + mesh)"), Result.CreatedActors.Num() >= 2);

	if (GEditor != nullptr && GEditor->Trans != nullptr)
	{
		TestEqual(
			TEXT("the import is the transaction Ctrl+Z would undo"),
			GEditor->Trans->GetUndoContext().Title.ToString(),
			TEXT("Import Mantle Place Vault Package"));
	}

	bool bRoadSplinesPlaceable = false;
	if (Manifest.RoadSplines.bEnginePresent || Manifest.RoadSplines.bPresent || Manifest.RoadSplines.bGpkgPresent)
	{
		int32 NumSplineActors = 0;
		for (const FString& Label : Result.CreatedActors)
		{
			if (Label.StartsWith(TEXT("MP_RoadSpline_")))
			{
				++NumSplineActors;
			}
		}
		const TArray<FMantlePlaceRoadSplinesCandidate> RoadPlan = Manifest.GetRoadSplinesPlan();
		const FMantlePlaceRoadSplinesCandidate* RoadSource = RoadPlan.FindByPredicate(
			[](const FMantlePlaceRoadSplinesCandidate& Candidate) { return Candidate.bPlaceable; });
		if (RoadSource != nullptr)
		{
			bRoadSplinesPlaceable = true;
			TestTrue(TEXT("road-splines bundle produced at least one spline actor"), NumSplineActors >= 1);
			const bool bEngineCopy = RoadSource->Source == EMantlePlaceRoadSplinesSource::EngineCopy;
			if (bEngineCopy || Manifest.RoadSplines.bGpkgPresent)
			{
				const FString& SourcePath = bEngineCopy ? Manifest.RoadSplines.EnginePath : Manifest.RoadSplines.GpkgPath;
				TestTrue(*FString::Printf(TEXT("the road splines were read from the GeoPackage %s"), *SourcePath),
					Result.Message.Contains(
						FMantlePlaceRoadSplinesLogic::DescribeSource(RoadSource->Source, SourcePath),
						ESearchCase::CaseSensitive));
			}
		}
		else
		{
			TestEqual(TEXT("a road layer skipped for its datum produced no spline actors"), NumSplineActors, 0);
			for (const FMantlePlaceRoadSplinesCandidate& Candidate : RoadPlan)
			{
				TestTrue(*FString::Printf(TEXT("the datum skip of the %s source is named in the import result, with its datum reason"),
							 FMantlePlaceRoadSplinesLogic::SourceName(Candidate.Source)),
					Result.Message.Contains(
						FString::Printf(TEXT("Road splines skipped: %s"), *Candidate.Refusal), ESearchCase::CaseSensitive));
			}
		}
	}

	UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		AddError(TEXT("No editor world available."));
		return false;
	}

	int32 NumLandscape = 0;
	ALandscape* Landscape = nullptr;
	for (TActorIterator<ALandscape> It(World); It; ++It)
	{
		++NumLandscape;
		if (Landscape == nullptr)
		{
			Landscape = *It;
		}
	}
	int32 NumMesh = 0;
	for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
	{
		++NumMesh;
	}
	TestTrue(TEXT("a Landscape exists in the level"), NumLandscape >= 1);
	TestTrue(TEXT("a StaticMeshActor exists in the level"), NumMesh >= 1);

	if (const FMantlePlaceLandscapeLayer* Weights = Manifest.FindLandscapeLayer(TEXT("material_weights")))
	{
		ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr;
		TestNotNull(TEXT("the imported landscape has a landscape info"), Info);
		if (Info != nullptr)
		{
			for (const FString& Material : Weights->Materials)
			{
				const bool bPainted = Info->Layers.ContainsByPredicate(
					[&Material](const FLandscapeInfoLayerSettings& Settings)
					{
						return Settings.LayerName == FName(*Material) && Settings.LayerInfoObj != nullptr;
					});
				TestTrue(FString::Printf(TEXT("material layer \"%s\" is on the landscape"), *Material), bPainted);
			}
		}
	}

	{
		TArray<FString> ExpectedLayers;
		for (const FMantlePlaceLandscapeLayer& Layer : Manifest.LandscapeLayers.Layers)
		{
			if (FMantlePlaceCoverageRasterLogic::IsCoverageRaster(Layer.Name) && Layer.UeReady.Num() > 0)
			{
				ExpectedLayers.Add(Layer.Name);
			}
		}

		TMap<FString, UTexture2D*> ByLayer;
		if (ExpectedLayers.Num() > 0)
		{
			const FString CoveragePath = MantlePlaceImportNaming::SubfolderPath(
				Result.ContentPath, MantlePlaceImportNaming::ESubfolder::CoverageRasters);
			FAssetRegistryModule& Registry =
				FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry"));
			TArray<FAssetData> Assets;
			Registry.Get().GetAssetsByPath(FName(*CoveragePath), Assets, true);
			for (const FAssetData& Asset : Assets)
			{
				UTexture2D* Texture = Cast<UTexture2D>(Asset.GetAsset());
				if (Texture == nullptr)
				{
					continue;
				}
				if (const UMantlePlaceCoverageRasterData* Data =
						Cast<UMantlePlaceCoverageRasterData>(Texture->GetAssetUserDataOfClass(
							UMantlePlaceCoverageRasterData::StaticClass())))
				{
					ByLayer.Add(Data->LayerName, Texture);
				}
			}
		}

		for (const FString& LayerName : ExpectedLayers)
		{
			UTexture2D** Found = ByLayer.Find(LayerName);
			TestNotNull(*FString::Printf(TEXT("coverage raster \"%s\" produced a texture"), *LayerName),
				Found != nullptr ? *Found : nullptr);
			if (Found == nullptr || *Found == nullptr)
			{
				continue;
			}
			UTexture2D* Texture = *Found;
			const UMantlePlaceCoverageRasterData* Data = Cast<UMantlePlaceCoverageRasterData>(
				Texture->GetAssetUserDataOfClass(UMantlePlaceCoverageRasterData::StaticClass()));

			TestFalse(*FString::Printf(TEXT("\"%s\" is not sRGB"), *LayerName), Texture->SRGB != 0);
			TestEqual(*FString::Printf(TEXT("\"%s\" has no mips"), *LayerName),
				Texture->MipGenSettings.GetValue(), TMGS_NoMipmaps);
			TestFalse(*FString::Printf(TEXT("\"%s\" is not virtual-textured"), *LayerName),
				Texture->VirtualTextureStreaming != 0);
			TestTrue(*FString::Printf(TEXT("\"%s\" records its source bundle"), *LayerName),
				Data != nullptr && Data->JobId == Manifest.JobId && !Data->SourcePath.IsEmpty());

			if (Data == nullptr || Data->Encoding != TEXT("png-16bit-grayscale"))
			{
				continue;
			}

			TestEqual(*FString::Printf(TEXT("\"%s\" landed as G16"), *LayerName),
				Texture->Source.GetFormat(), TSF_G16);
			TestEqual(*FString::Printf(TEXT("\"%s\" uses TC_Grayscale (-> PF_G16)"), *LayerName),
				Texture->CompressionSettings.GetValue(), TC_Grayscale);

			TArray64<uint8> Mip;
			if (!Texture->Source.GetMipData(Mip, 0) || Mip.Num() < 2)
			{
				AddError(FString::Printf(TEXT("Could not read source pixels for \"%s\"."), *LayerName));
				continue;
			}

			const int64 NumSamples = Mip.Num() / 2;
			bool bAnyLowByteSet = false;
			double MinDecoded = TNumericLimits<double>::Max();
			double MaxDecoded = TNumericLimits<double>::Lowest();
			const uint16* Samples = reinterpret_cast<const uint16*>(Mip.GetData());
			for (int64 Index = 0; Index < NumSamples; ++Index)
			{
				const uint16 Sample = Samples[Index];
				bAnyLowByteSet |= (Sample & 0xFF) != 0;
				const double Value = Data->MinValue + (static_cast<double>(Sample) / 65535.0)
					* (Data->MaxValue - Data->MinValue);
				MinDecoded = FMath::Min(MinDecoded, Value);
				MaxDecoded = FMath::Max(MaxDecoded, Value);
			}
			TestTrue(*FString::Printf(
					TEXT("\"%s\" carries true 16-bit precision (some sample has a non-zero low byte)"), *LayerName),
				bAnyLowByteSet);

			TestTrue(*FString::Printf(TEXT("\"%s\" decodes within its declared range [%f, %f]"),
					*LayerName, Data->MinValue, Data->MaxValue),
				MinDecoded >= Data->MinValue - KINDA_SMALL_NUMBER
					&& MaxDecoded <= Data->MaxValue + KINDA_SMALL_NUMBER);
			AddInfo(FString::Printf(TEXT("coverage raster \"%s\": %lld samples, decoded %.3f..%.3f %s"),
				*LayerName, NumSamples, MinDecoded, MaxDecoded,
				Data->Units.IsEmpty() ? TEXT("(unitless)") : *Data->Units));
		}
	}

	if (Landscape != nullptr)
	{
		const TArray<ULandscapeComponent*> Components =
			MantlePlaceImportLiveTest::GatherLandscapeComponents(Landscape);
		if (TestTrue(TEXT("the imported landscape has at least one component to read the drape from"),
				Components.Num() > 0))
		{
			ULandscapeComponent* Component = Components[0];
			UMaterialInstance* Instance = Component->GetMaterialInstance(0, false);
			UMaterial* Base = Instance ? Instance->GetMaterial() : nullptr;
			TestNotNull(TEXT("landscape component has a material instance"), Instance);
			const bool bMaterialIsDrape = Base != nullptr && Base != UMaterial::GetDefaultMaterial(MD_Surface);
			const FString DrapeFailure = FMantlePlaceLiveGateLogic::JudgeDrape(Manifest, Result.Message, bMaterialIsDrape);
			if (!Manifest.Drape.bPresent)
			{
				AddInfo(FString::Printf(TEXT("Imagery drape not placed by the import; checked its report instead: %s"),
					*FMantlePlaceLiveGateLogic::DrapeSkipLine(Manifest)));
			}
			TestTrue(Manifest.Drape.bPresent
					? TEXT("landscape component material is not the engine default surface (the drape was placed)")
					: TEXT("the import reports the imagery drape it did not place"),
				DrapeFailure.IsEmpty());
			if (!DrapeFailure.IsEmpty())
			{
				AddError(DrapeFailure);
			}
		}
	}

	if (Landscape != nullptr && !Manifest.Heightmap.Path.IsEmpty())
	{
		using namespace MantlePlaceImportLiveTest;
		constexpr int32 GridN = 8;

		TArray<double> Source;
		if (IFileHandle* Handle = Platform.OpenRead(*Zip))
		{
			FZipArchiveReader Reader(Handle);
			TArray<uint8> Png;
			if (!Reader.TryReadFile(Manifest.Heightmap.Path, Png))
			{
				AddError(FString::Printf(TEXT("Orientation gate: bundle has no entry \"%s\"."),
					*Manifest.Heightmap.Path));
			}
			else
			{
				IImageWrapperModule& Wrappers =
					FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
				TSharedPtr<IImageWrapper> Wrapper = Wrappers.CreateImageWrapper(EImageFormat::PNG);
				TArray64<uint8> Raw;
				if (!Wrapper.IsValid()
					|| !Wrapper->SetCompressed(Png.GetData(), Png.Num())
					|| !Wrapper->GetRaw(ERGBFormat::Gray, 16, Raw))
				{
					AddError(TEXT("Orientation gate: could not decode the source heightmap as 16-bit grey."));
				}
				else
				{
					const int32 Width = Wrapper->GetWidth();
					const int32 Height = Wrapper->GetHeight();
					const uint16* Pixels = reinterpret_cast<const uint16*>(Raw.GetData());
					Source.SetNumUninitialized(GridN * GridN);
					for (int32 I = 0; I < GridN; ++I)
					{
						for (int32 J = 0; J < GridN; ++J)
						{
							const int32 FromNorth = CellCentre(I, GridN, Height);
							const int32 Row = Manifest.Heightmap.bRow0IsNorth ? FromNorth : (Height - 1 - FromNorth);
							const int32 Col = CellCentre(J, GridN, Width);
							Source[I * GridN + J] =
								static_cast<double>(Pixels[static_cast<int64>(Row) * Width + Col]);
						}
					}
				}
			}
		}
		else
		{
			AddError(FString::Printf(TEXT("Orientation gate: could not reopen the bundle \"%s\"."), *Zip));
		}

		TArray<double> Landed;
		{
			TArray<FVector> Samples;
			constexpr int32 VertexStride = 8;
			const TArray<ULandscapeComponent*> Components = GatherLandscapeComponents(Landscape);
			int32 NumWithoutHeightData = 0;
			for (ULandscapeComponent* Component : Components)
			{
				FLandscapeComponentDataInterface Data(Component, 0, false);
				if (Data.GetRawHeightData() == nullptr)
				{
					++NumWithoutHeightData;
					continue;
				}
				const int32 SizeVerts = Component->ComponentSizeQuads + 1;
				for (int32 LocalY = 0; LocalY < SizeVerts; LocalY += VertexStride)
				{
					for (int32 LocalX = 0; LocalX < SizeVerts; LocalX += VertexStride)
					{
						Samples.Add(Data.GetWorldVertex(LocalX, LocalY));
					}
				}
			}

			if (Samples.Num() < GridN * GridN * 4)
			{
				AddError(FString::Printf(
					TEXT("Orientation gate: only %d landscape vertices were readable "
						 "(%d component(s) via ULandscapeInfo, %d of them with no raw height data)."),
					Samples.Num(), Components.Num(), NumWithoutHeightData));
			}
			else
			{
				double MinWorldX = TNumericLimits<double>::Max(), MaxWorldX = TNumericLimits<double>::Lowest();
				double MinWorldY = TNumericLimits<double>::Max(), MaxWorldY = TNumericLimits<double>::Lowest();
				for (const FVector& Sample : Samples)
				{
					MinWorldX = FMath::Min(MinWorldX, Sample.X);
					MaxWorldX = FMath::Max(MaxWorldX, Sample.X);
					MinWorldY = FMath::Min(MinWorldY, Sample.Y);
					MaxWorldY = FMath::Max(MaxWorldY, Sample.Y);
				}
				const double SpanX = MaxWorldX - MinWorldX;
				const double SpanY = MaxWorldY - MinWorldY;
				if (SpanX <= 0.0 || SpanY <= 0.0)
				{
					AddError(TEXT("Orientation gate: the imported landscape has no world extent."));
				}
				else
				{
					TArray<double> Sum;
					TArray<int32> Count;
					Sum.SetNumZeroed(GridN * GridN);
					Count.SetNumZeroed(GridN * GridN);
					for (const FVector& Sample : Samples)
					{
						const int32 I = FMath::Clamp(
							FMath::FloorToInt32((MaxWorldX - Sample.X) / SpanX * GridN), 0, GridN - 1);
						const int32 J = FMath::Clamp(
							FMath::FloorToInt32((Sample.Y - MinWorldY) / SpanY * GridN), 0, GridN - 1);
						Sum[I * GridN + J] += Sample.Z;
						Count[I * GridN + J] += 1;
					}

					bool bEveryCellCovered = true;
					Landed.SetNumUninitialized(GridN * GridN);
					for (int32 Cell = 0; Cell < GridN * GridN; ++Cell)
					{
						bEveryCellCovered &= Count[Cell] > 0;
						Landed[Cell] = Count[Cell] > 0 ? Sum[Cell] / Count[Cell] : 0.0;
					}
					if (!bEveryCellCovered)
					{
						AddError(TEXT("Orientation gate: the landscape did not cover every probe cell."));
						Landed.Reset();
					}
				}
			}
		}

		if (Source.Num() == GridN * GridN && Landed.Num() == GridN * GridN)
		{
			EDihedral Best = EDihedral::Identity;
			double BestCorr = -2.0;
			double RunnerUp = -2.0;
			double IdentityCorr = 0.0;
			for (uint8 Which = 0; Which < static_cast<uint8>(EDihedral::Count); ++Which)
			{
				const EDihedral Transform = static_cast<EDihedral>(Which);
				const double Score = Correlation(Landed, TransformGrid(Source, GridN, Transform));
				AddInfo(FString::Printf(TEXT("orientation %-15s corr=%+.4f"), DihedralName(Transform), Score));
				if (Transform == EDihedral::Identity)
				{
					IdentityCorr = Score;
				}
				if (Score > BestCorr)
				{
					RunnerUp = BestCorr;
					BestCorr = Score;
					Best = Transform;
				}
				else if (Score > RunnerUp)
				{
					RunnerUp = Score;
				}
			}

			TestEqual(
				TEXT("the imported landscape matches the source heightmap under NO dihedral transform "
					 "(North -> +X, East -> +Y)"),
				FString(DihedralName(Best)), FString(DihedralName(EDihedral::Identity)));
			TestTrue(
				*FString::Printf(TEXT("... and matches it closely (identity corr %+.4f >= 0.90)"), IdentityCorr),
				IdentityCorr >= 0.90);
			AddInfo(FString::Printf(
				TEXT("orientation gate: best '%s' %+.4f, runner-up %+.4f (margin %.4f)"),
				DihedralName(Best), BestCorr, RunnerUp, BestCorr - RunnerUp));
		}
	}

	if (Landscape != nullptr)
	{
		using namespace MantlePlaceImportLiveTest;
		constexpr int32 WantedPoints = 64;

		TArray<FCheckPoint> CheckPoints;
		FString NoPoints;
		bool bRead = false;
		bool bNotStatedBy3x = false;
		if (IFileHandle* Handle = Platform.OpenRead(*Zip))
		{
			FZipArchiveReader Reader(Handle);
			bRead = ReadCheckPoints(Reader, WantedPoints, CheckPoints, NoPoints, bNotStatedBy3x);
		}
		else
		{
			NoPoints = FString::Printf(TEXT("could not reopen the bundle \"%s\""), *Zip);
		}

		if (!bRead && bNotStatedBy3x)
		{
			AddInfo(FString::Printf(TEXT("Placement gate not applicable to a 3.x bundle: %s."), *NoPoints));
		}
		else if (!bRead)
		{
			const FString Reason = FString::Printf(TEXT("Placement gate not run: %s."), *NoPoints);
			if (bStrict)
			{
				AddError(Reason);
			}
			else
			{
				AddWarning(Reason);
			}
		}
		else
		{
			TArray<FVector> Vertices;
			for (ULandscapeComponent* Component : MantlePlaceImportLiveTest::GatherLandscapeComponents(Landscape))
			{
				FLandscapeComponentDataInterface Data(Component, 0, false);
				if (Data.GetRawHeightData() == nullptr)
				{
					continue;
				}
				const int32 SizeVerts = Component->ComponentSizeQuads + 1;
				for (int32 LocalY = 0; LocalY < SizeVerts; ++LocalY)
				{
					for (int32 LocalX = 0; LocalX < SizeVerts; ++LocalX)
					{
						Vertices.Add(Data.GetWorldVertex(LocalX, LocalY));
					}
				}
			}

			FBox2D Extent(ForceInit);
			for (const FVector& Vertex : Vertices)
			{
				Extent += FVector2D(Vertex.X, Vertex.Y);
			}
			const FBox2D Inner = Extent.ExpandBy(-100.0);

			int32 Measured = 0;
			int32 Outside = 0;
			FMantlePlaceDzStatistics DzStats;
			double MaxDistance = 0.0;
			for (const FCheckPoint& Point : CheckPoints)
			{
				if (!Inner.IsInside(FVector2D(Point.XCm, Point.YCm)))
				{
					++Outside;
					continue;
				}
				double BestD2 = TNumericLimits<double>::Max();
				double GroundZ = 0.0;
				for (const FVector& Vertex : Vertices)
				{
					const double D2 = FMath::Square(Vertex.X - Point.XCm) + FMath::Square(Vertex.Y - Point.YCm);
					if (D2 < BestD2)
					{
						BestD2 = D2;
						GroundZ = Vertex.Z;
					}
				}
				const double Dz = GroundZ - Point.ExpectedZCm;
				++Measured;
				DzStats.Add(Dz);
				MaxDistance = FMath::Max(MaxDistance, FMath::Sqrt(BestD2));
			}

			TestTrue(*FString::Printf(TEXT("placement gate: at least half of the %d check points fall on the landscape (%d did)"),
						 CheckPoints.Num(), Measured),
				Measured * 2 >= CheckPoints.Num());
			if (Measured > 0)
			{
				const double Mean = DzStats.Mean();
				const double Rms = DzStats.Rms();
				AddInfo(FString::Printf(
					TEXT("placement gate: %d check points (%d outside), landscape minus published ground: "
						 "mean %+.3f m, RMS %.3f m, max |dz| %.3f m (information only), nearest vertex within %.3f m"),
					Measured, Outside, Mean / 100.0, Rms / 100.0, DzStats.MaxAbs / 100.0, MaxDistance / 100.0));
				TestTrue(*FString::Printf(TEXT("placement gate: no systematic offset, |mean| within %.2f m (mean %+.3f m)"),
							 FMantlePlaceLiveGateLogic::MaxMeanOffsetCm / 100.0, Mean / 100.0),
					FMantlePlaceLiveGateLogic::MeanWithinTolerance(DzStats));
				TestTrue(*FString::Printf(TEXT("placement gate: spread, RMS within %.2f m (RMS %.3f m)"),
							 FMantlePlaceLiveGateLogic::MaxRmsCm / 100.0, Rms / 100.0),
					FMantlePlaceLiveGateLogic::RmsWithinTolerance(DzStats));
			}
		}
	}

	if (Landscape != nullptr && bRoadSplinesPlaceable)
	{
		constexpr int32 WantedRoadPoints = 128;
		constexpr double RoadMedianToleranceCm = 100.0;

		TArray<FVector> RoadPoints;
		for (TActorIterator<AMantlePlaceRoadSpline> It(World); It; ++It)
		{
			if (!It->GetActorLabel().StartsWith(TEXT("MP_RoadSpline_")) || It->Spline == nullptr)
			{
				continue;
			}
			const int32 NumPoints = It->Spline->GetNumberOfSplinePoints();
			for (int32 Index = 0; Index < NumPoints; ++Index)
			{
				RoadPoints.Add(It->Spline->GetLocationAtSplinePoint(Index, ESplineCoordinateSpace::World));
			}
		}

		TArray<FVector> Vertices;
		for (ULandscapeComponent* Component : MantlePlaceImportLiveTest::GatherLandscapeComponents(Landscape))
		{
			FLandscapeComponentDataInterface Data(Component, 0, false);
			if (Data.GetRawHeightData() == nullptr)
			{
				continue;
			}
			const int32 SizeVerts = Component->ComponentSizeQuads + 1;
			for (int32 LocalY = 0; LocalY < SizeVerts; ++LocalY)
			{
				for (int32 LocalX = 0; LocalX < SizeVerts; ++LocalX)
				{
					Vertices.Add(Data.GetWorldVertex(LocalX, LocalY));
				}
			}
		}
		FBox2D Extent(ForceInit);
		for (const FVector& Vertex : Vertices)
		{
			Extent += FVector2D(Vertex.X, Vertex.Y);
		}
		const FBox2D Inner = Extent.ExpandBy(-100.0);

		TArray<FVector> Inside;
		for (const FVector& Point : RoadPoints)
		{
			if (Inner.IsInside(FVector2D(Point.X, Point.Y)))
			{
				Inside.Add(Point);
			}
		}
		TestTrue(*FString::Printf(TEXT("road gate: the placed roads have points on the landscape (%d of %d)"),
					 Inside.Num(), RoadPoints.Num()),
			Inside.Num() > 0);

		TArray<double> AbsDz;
		double SumDz = 0.0;
		const int32 Stride = FMath::Max(1, Inside.Num() / WantedRoadPoints);
		for (int32 Index = Stride / 2; Index < Inside.Num() && AbsDz.Num() < WantedRoadPoints; Index += Stride)
		{
			const FVector& Point = Inside[Index];
			double BestD2 = TNumericLimits<double>::Max();
			double GroundZ = 0.0;
			for (const FVector& Vertex : Vertices)
			{
				const double D2 = FMath::Square(Vertex.X - Point.X) + FMath::Square(Vertex.Y - Point.Y);
				if (D2 < BestD2)
				{
					BestD2 = D2;
					GroundZ = Vertex.Z;
				}
			}
			const double Dz = Point.Z - GroundZ;
			SumDz += Dz;
			AbsDz.Add(FMath::Abs(Dz));
		}
		if (!AbsDz.IsEmpty())
		{
			AbsDz.Sort();
			const double Median = AbsDz[AbsDz.Num() / 2];
			const double P90 = AbsDz[FMath::Min(AbsDz.Num() - 1, (AbsDz.Num() * 9) / 10)];
			AddInfo(FString::Printf(
				TEXT("road gate: %d road points on the landscape, %d measured; road minus ground: mean %+.3f m, "
					 "median |dz| %.3f m, p90 |dz| %.3f m, max |dz| %.3f m"),
				Inside.Num(), AbsDz.Num(), SumDz / AbsDz.Num() / 100.0, Median / 100.0, P90 / 100.0,
				AbsDz.Last() / 100.0));
			TestTrue(*FString::Printf(TEXT("road gate: the median road point is within %.1f m of the ground (%.3f m)"),
						 RoadMedianToleranceCm / 100.0, Median / 100.0),
				Median <= RoadMedianToleranceCm);
		}
	}

	return true;
}

#endif
