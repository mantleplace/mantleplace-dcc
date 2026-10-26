// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest (base-bundle skip + layer presence)
#include "MantlePlaceImportNaming.h"   // subfolder layout, so this asserts the standard not a copy of it
#include "MantlePlaceVaultTypes.h"     // MantlePlaceMinSupportedManifestVersion (stale-fixture skip)
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceRoadSplinesLogic.h" // the importer's own datum decision, asked not recomputed
#include "MantlePlaceRoadSpline.h"       // the road gate reads the placed splines back
#include "MantlePlaceCoverageRasterLogic.h"  // IsCoverageRaster (which layers to expect)
#include "MantlePlaceCoverageRasterTypes.h"  // runtime: UMantlePlaceCoverageRasterData

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Editor/Transactor.h" // the import must land as ONE undoable transaction
#include "Engine/Texture2D.h"
#include "HAL/PlatformMisc.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h" // orientation gate: re-read the source heightmap
#include "HAL/PlatformFileManager.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Landscape.h"
#include "LandscapeComponent.h"
#include "LandscapeDataAccess.h" // FLandscapeComponentDataInterface (orientation gate)
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
	/**
	 * Pearson correlation over two equal-length samples. Deliberately offset- and scale-invariant:
	 * the source is raw uint16 and the landing is landscape height units through a Z scale and a
	 * location offset, so a correlation cannot be faked or masked by any of that — only the
	 * ORIENTATION is under test. Returns 0.0 for a degenerate (constant) input rather than NaN.
	 */
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

	/** The dihedral group of a square grid — every way an orientation bug can present. */
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

	/** Re-index a row-major NxN grid by one of the eight dihedral transforms. */
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

	/** Centre-of-cell index for bucket Index of Count, over Extent samples. */
	int32 CellCentre(int32 Index, int32 Count, int32 Extent)
	{
		return FMath::Clamp(FMath::RoundToInt32((Index + 0.5) * Extent / Count), 0, Extent - 1);
	}

	/**
	 * Every landscape component of `Landscape`, wherever the world happened to put it.
	 *
	 * Deliberately NOT `ALandscape::LandscapeComponents`. That array holds only the components
	 * owned by the ALandscape actor itself, and a World Partition world distributes them across
	 * ALandscapeStreamingProxy actors instead -- so reading the actor's own array there yields an
	 * empty set and every readback below silently measures nothing. ULandscapeInfo is the
	 * whole-landscape view: ForAllLandscapeComponents walks the ALandscape *and* every loaded
	 * streaming proxy, making it a superset of the actor array in both world configurations. It
	 * also null-filters, so what comes back needs no further guarding.
	 *
	 * "Loaded" is the standing caveat: an unloaded World Partition cell contributes components to
	 * nobody, so a caller needing the whole landscape must keep its cells loaded.
	 */
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

	/**
	 * Is this a CI run? Keyed on `CI`, which every mainstream CI provider sets and which no
	 * developer shell sets by accident; presence is the signal, the value is not read.
	 *
	 * Strictness matters because a warn-and-pass skip and a gate that ran and passed are the same
	 * green. That is not hypothetical: the orientation gate below shipped unable to run on CI and
	 * went unnoticed until it was audited. Under CI a skip is therefore an error — the same rule
	 * the conformance corpus already lives by, where a missing corpus is a failure and never a skip.
	 */
	bool IsCi()
	{
		return !FPlatformMisc::GetEnvironmentVariable(TEXT("CI")).IsEmpty();
	}

	/** One published ground point, in this host's frame: world X north, world Y east, Z in the landscape's datum. */
	struct FCheckPoint
	{
		double XCm = 0.0;
		double YCm = 0.0;
		double ExpectedZCm = 0.0;
	};

	/** The datum of every height the landscape carries; a check point stated in another is shifted into it. */
	const TCHAR* const LandscapeDatum = TEXT("EGM2008-orthometric");

	/**
	 * The bundle's own surface points (`elevation.points_csv`), as check points for the landscape.
	 *
	 * Read from the manifest this test re-opens, not from the importer's parse: the importer never reads
	 * these points, so they are an independent statement of where the ground is, cut from the DEM rather
	 * than from the heightmap the landscape is built from. They are local ENU offsets from the AOI
	 * centroid, which is the world origin here, so the expected position is the published one with no
	 * arithmetic but the axis order (north to +X, east to +Y) and metres to centimetres. A point stated in
	 * NAVD88 is moved into the landscape's EGM2008 by the bundle's own `navd88_minus_egm2008_m`, verbatim;
	 * a bundle that does not publish that difference has no check points this test can trust.
	 *
	 * `Wanted` points are taken evenly through the file. Returns false, with the reason, when the bundle
	 * publishes no points this test can read.
	 */
	bool ReadCheckPoints(FZipArchiveReader& Reader, int32 Wanted, TArray<FCheckPoint>& Out, FString& OutReason)
	{
		Out.Reset();
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
			Point.YCm = FCString::Atod(*Cells[0]) * 100.0; // east
			Point.XCm = FCString::Atod(*Cells[1]) * 100.0; // north
			Point.ExpectedZCm = (FCString::Atod(*Cells[2]) + DatumShiftM) * 100.0;
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

// Live end-to-end import against a real downloaded sample bundle. Runs in the editor
// (needs an editor world). Skips (with a warning) when the sample isn't present, so it
// stays portable — EXCEPT under CI, where every skip below is an error instead, because a
// skipped gate and a passed gate are otherwise the same green. It intentionally leaves the
// imported actors in the level so the result can be inspected / screenshotted.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportLiveTest,
	"MantlePlace.Import.LiveFreeTier",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportLiveTest::RunTest(const FString& Parameters)
{
	// MP_LIVETEST_BUNDLE overrides; otherwise the in-tree live-test copy. No developer-machine
	// fallback paths — a missing bundle is a warn-and-pass skip locally and an error on CI (below).
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();

	// Every skip in this test routes through here: warn-and-pass locally, error on CI. Returning
	// the value RunTest should return keeps each skip site a single line, so a future skip branch
	// added without the strict path would stand out as the one that does not use it.
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

	// A base_on_demand bundle whose Unreal formats haven't materialized yet can't exercise the
	// import — skip loudly rather than fail, so a freshly procured base bundle is usable as the
	// fixture the moment its materialize completes.
	FMantlePlaceVaultManifest Manifest;
	{
		FString ReadError;
		const bool bRead = UMantlePlaceImporterLibrary::ReadVaultManifest(Zip, Manifest, ReadError);

		// A fixture cut before the current clean-break floor is stale, not broken. Every floor bump
		// invalidates every bundle downloaded before it, so failing here would turn a routine bump
		// into a red local suite on every machine holding an older zip — the same warn-and-skip the
		// missing-bundle and base_on_demand cases get, for the same portability reason.
		// Through the version helper, never through FString's `<`. Both operands are strings now, so
		// a raw comparison compiles and is lexicographic: the pre-history's "19" sorts ABOVE "1.0.0"
		// on the second character, and exactly the stale fixture this branch exists to skip would
		// fall through to a hard failure instead.
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

	const FMantlePlaceImportResult Result =
		UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);

	AddInfo(FString::Printf(TEXT("jobId=%s actors=%d\n%s"),
		*Result.JobId, Result.CreatedActors.Num(), *Result.Message));

	TestTrue(TEXT("import reported success"), Result.bSuccess);
	TestTrue(TEXT("created at least 2 actors (landscape + mesh)"), Result.CreatedActors.Num() >= 2);

	// The import is one undoable transaction, and it is the one Ctrl+Z takes. The claim is in
	// ImportVaultPackage's own doc comment, and it was false for as long as the importer renamed a
	// freshly-imported asset inside its own FScopedTransaction: FAssetRenameManager deletes the
	// object it moved away from, ObjectTools sees that object is referenced only by the undo buffer
	// and resets the WHOLE buffer, and the half-built import transaction goes with it. Measured
	// 2026-08-30 on a 523-actor bundle: Ctrl+Z moved nothing, twice, and the editor log carried
	// "Non zero active count in UTransBuffer::Reset / Reason: Delete Selected Item".
	//
	// Asserted by TITLE rather than by undoing: the test deliberately leaves the actors in the level
	// for inspection, and an assertion that has to mutate the thing it is inspecting is one nobody
	// can run twice. GetUndoContext names exactly what the next Ctrl+Z would take.
	if (GEditor != nullptr && GEditor->Trans != nullptr)
	{
		TestEqual(
			TEXT("the import is the transaction Ctrl+Z would undo"),
			GEditor->Trans->GetUndoContext().Title.ToString(),
			TEXT("Import Mantle Place Vault Package"));
	}

	// New-layer assertions fire only when the bundle actually ships the layer. Any source declares it:
	// a 2.0 bundle publishes the road layer as a GeoPackage alone, with no GeoJSON row, and a check
	// keyed on the GeoJSON row passed over every 2.0 bundle's roads without asserting anything. The
	// Unreal block's own copy (MPB 1.16.0) is a source too, the one a US order's roads come from.
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
		// Assert on what the importer decided, not on the layer being present: a source whose heights
		// are not in this landscape's datum is a named skip (#278), never shifted, so it places nothing.
		// The decision is asked of the importer's own plan, not re-derived here: the Unreal block's copy
		// first, then the shared layer, the first placeable source being the one the roads come from.
		const TArray<FMantlePlaceRoadSplinesCandidate> RoadPlan = Manifest.GetRoadSplinesPlan();
		const FMantlePlaceRoadSplinesCandidate* RoadSource = RoadPlan.FindByPredicate(
			[](const FMantlePlaceRoadSplinesCandidate& Candidate) { return Candidate.bPlaceable; });
		if (RoadSource != nullptr)
		{
			bRoadSplinesPlaceable = true;
			TestTrue(TEXT("road-splines bundle produced at least one spline actor"), NumSplineActors >= 1);
			// The source is read from its GeoPackage first; a bundle that declares one must have its roads
			// from it, not from the GeoJSON fallback the importer takes when the GeoPackage fails. The
			// engine copy, when the manifest publishes one, is the source and its line says so.
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
			// The importer writes this same "Road splines skipped" prefix for a missing file and for a
			// parse error, so the prefix alone would pass for the wrong reason. Match the datum reason
			// each declared source was refused with, the exact entry the importer logs for the skip.
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

	// Material-weight layers: the bundle's `unreal.landscape_layers.material_weights` legend must
	// arrive as named, layer-info-backed weightmap layers on the imported Landscape. This is the
	// assertion guards — the block was published and produced nothing — so it checks the
	// engine's own layer map rather than the importer's return value.
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

	// Coverage rasters: every landscape layer that is not material_weights must arrive as a texture
	// carrying its meaning. Asserting the assets EXIST would not be worth running — a silently
	// 8-bit `slope` passes that and is wrong in a way no consumer can see. So this reads the landed
	// source format and the actual bytes.
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
			// From the result, not rebuilt here: the import decides where its content went (the order,
			// not the job, and beneath a configurable root), and a test that re-derives that path is a
			// test that passes while the importer writes somewhere else.
			const FString CoveragePath = MantlePlaceImportNaming::SubfolderPath(
				Result.ContentPath, MantlePlaceImportNaming::ESubfolder::CoverageRasters);
			FAssetRegistryModule& Registry =
				FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry"));
			TArray<FAssetData> Assets;
			Registry.Get().GetAssetsByPath(FName(*CoveragePath), Assets, /*bRecursive*/ true);
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

			// The settings that make it readable as data rather than as colour.
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

			// A 16-bit channel must have LANDED 16-bit. TC_Grayscale on anything else silently
			// yields G8, and the values would look entirely plausible.
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

			// The precision proof. Data that had been quantised to 8 bits and widened back would
			// have a zero low byte in every sample; real 16-bit data does not. This is the
			// assertion that an "assets exist" test would have let through.
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

			// And the mapping decodes those samples into the range the manifest declared — for
			// `slope` and `aspect` that is degrees, for `canopy_height` metres.
			TestTrue(*FString::Printf(TEXT("\"%s\" decodes within its declared range [%f, %f]"),
					*LayerName, Data->MinValue, Data->MaxValue),
				MinDecoded >= Data->MinValue - KINDA_SMALL_NUMBER
					&& MaxDecoded <= Data->MaxValue + KINDA_SMALL_NUMBER);
			AddInfo(FString::Printf(TEXT("coverage raster \"%s\": %lld samples, decoded %.3f..%.3f %s"),
				*LayerName, NumSamples, MinDecoded, MaxDecoded,
				Data->Units.IsEmpty() ? TEXT("(unitless)") : *Data->Units));
		}
	}

	// Drape smoke check: the imagery drape material must reach the landscape's render material — the
	// component must NOT be left on the engine default surface (the symptom of the "material needs a
	// manual re-apply" bug). This guards the plumbing headlessly; pixel-accurate render timing (does it
	// show on the import frame with no re-apply / refresh) is verified live in the editor.
	//
	// The component set is ASSERTED, not used as a silent precondition. This block used to open
	// `if (Landscape->LandscapeComponents.Num() > 0)` and fall straight through an empty array --
	// so in exactly the world configuration that hides the components from that array (World
	// Partition, which owns them via streaming proxies) the drape check went vacuous rather than
	// red. A skipped gate and a passed gate are the same green; this one now fails loudly.
	if (Landscape != nullptr)
	{
		const TArray<ULandscapeComponent*> Components =
			MantlePlaceImportLiveTest::GatherLandscapeComponents(Landscape);
		if (TestTrue(TEXT("the imported landscape has at least one component to read the drape from"),
				Components.Num() > 0))
		{
			ULandscapeComponent* Component = Components[0];
			UMaterialInstance* Instance = Component->GetMaterialInstance(0, /*InDynamic*/ false);
			UMaterial* Base = Instance ? Instance->GetMaterial() : nullptr;
			TestNotNull(TEXT("landscape component has a material instance"), Instance);
			TestTrue(TEXT("landscape component material is not the engine default surface"),
				Base != nullptr && Base != UMaterial::GetDefaultMaterial(MD_Surface));
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Orientation gate (North -> +X, East -> +Y).
	//
	// Everything above this point passes just as happily on a mirrored, transposed or 180-rotated
	// import: it asserts that assets exist and carry their meaning, never that they landed the right
	// way round. That is the hole this closes.
	//
	// It compares an NxN downsample of the SOURCE heightmap, read in map order (row 0 north, column 0
	// west), against the same downsample of the IMPORTED landscape, read in world order (landscape-
	// local X north, local Y east). Scoring all eight dihedral transforms — not just the identity — is
	// what makes it a gate: on near-symmetric terrain a merely-high identity score can sit underneath
	// an even higher mirrored one, so "identity wins" is the assertion, and the runner-up is logged.
	// ---------------------------------------------------------------------------------------------
	if (Landscape != nullptr && !Manifest.Heightmap.Path.IsEmpty())
	{
		using namespace MantlePlaceImportLiveTest;
		constexpr int32 GridN = 8; // 64 probes: enough to discriminate, cheap to read back

		// --- the source heightmap, in map order (I north->south, J west->east) ---
		TArray<double> Source;
		if (IFileHandle* Handle = Platform.OpenRead(*Zip))
		{
			FZipArchiveReader Reader(Handle); // takes ownership of the handle
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
							// I counts southward from the north edge; flip when row 0 is the SOUTH edge.
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

		// --- the imported landscape, sampled in WORLD space ---
		// Deliberately world-space rather than landscape-local: GetWorldVertex runs the engine's own
		// transform, so the actor's rotation and any negative scale are inside what is being tested,
		// not assumed away. Cells are then binned by world +X = North and +Y = East, which IS the
		// claim under test.
		TArray<double> Landed;
		{
			TArray<FVector> Samples;
			constexpr int32 VertexStride = 8;
			// Read through ULandscapeInfo, not ALandscape::LandscapeComponents -- see
			// GatherLandscapeComponents. The two tallies below exist so that a failure says WHICH way it
			// broke: no components reached us at all (they are owned somewhere this view cannot see, or
			// the landscape never registered) versus components arrived but every one of them had no
			// readable height data (an edit-layer / flush problem). Those two want opposite fixes, and
			// the bare vertex count that used to be reported here could not tell them apart.
			const TArray<ULandscapeComponent*> Components = GatherLandscapeComponents(Landscape);
			int32 NumWithoutHeightData = 0;
			for (ULandscapeComponent* Component : Components)
			{
				FLandscapeComponentDataInterface Data(Component, /*MipLevel*/ 0, /*WorkOnEditingLayer*/ false);
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
						// I counts southward from the north edge => descending world X.
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

	// ---------------------------------------------------------------------------------------------
	// Placement gate: the landscape stands where the bundle says the ground is.
	//
	// The orientation gate above is offset- and scale-invariant on purpose, so a landscape that is the
	// right way round but sits a hundred metres east, or a metre too low, passes it. This closes that:
	// the bundle's own surface points, cut from the DEM and never read by the importer, are known check
	// points, and each must find the landscape's ground within a metre of its published height at its
	// published position. The landscape is a 1 m grid, so the nearest vertex is within 0.71 m of a
	// point, and a metre holds that plus the slope across it; a placement off by a post, a datum or a
	// flip does not fit inside it, and the mean and RMS are logged so a drift shows before it fails.
	// ---------------------------------------------------------------------------------------------
	if (Landscape != nullptr)
	{
		using namespace MantlePlaceImportLiveTest;
		constexpr int32 WantedPoints = 64;
		constexpr double ToleranceCm = 100.0;

		TArray<FCheckPoint> CheckPoints;
		FString NoPoints;
		bool bRead = false;
		if (IFileHandle* Handle = Platform.OpenRead(*Zip))
		{
			FZipArchiveReader Reader(Handle); // takes ownership of the handle
			bRead = ReadCheckPoints(Reader, WantedPoints, CheckPoints, NoPoints);
		}
		else
		{
			NoPoints = FString::Printf(TEXT("could not reopen the bundle \"%s\""), *Zip);
		}

		if (!bRead)
		{
			// The skip rule the rest of this test lives by: a warning locally, an error under CI.
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
				FLandscapeComponentDataInterface Data(Component, /*MipLevel*/ 0, /*WorkOnEditingLayer*/ false);
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
			const FBox2D Inner = Extent.ExpandBy(-100.0); // a point on the very edge has no ground on one side

			int32 Measured = 0;
			int32 Outside = 0;
			double SumDz = 0.0;
			double SumDz2 = 0.0;
			double MaxAbsDz = 0.0;
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
				SumDz += Dz;
				SumDz2 += Dz * Dz;
				MaxAbsDz = FMath::Max(MaxAbsDz, FMath::Abs(Dz));
				MaxDistance = FMath::Max(MaxDistance, FMath::Sqrt(BestD2));
			}

			TestTrue(*FString::Printf(TEXT("placement gate: at least half of the %d check points fall on the landscape (%d did)"),
						 CheckPoints.Num(), Measured),
				Measured * 2 >= CheckPoints.Num());
			if (Measured > 0)
			{
				const double Mean = SumDz / Measured;
				const double Rms = FMath::Sqrt(SumDz2 / Measured);
				AddInfo(FString::Printf(
					TEXT("placement gate: %d check points (%d outside), landscape minus published ground: "
						 "mean %+.3f m, RMS %.3f m, max |dz| %.3f m, nearest vertex within %.3f m"),
					Measured, Outside, Mean / 100.0, Rms / 100.0, MaxAbsDz / 100.0, MaxDistance / 100.0));
				TestTrue(*FString::Printf(TEXT("placement gate: every check point within %.1f m of the ground (max %.3f m)"),
							 ToleranceCm / 100.0, MaxAbsDz / 100.0),
					MaxAbsDz <= ToleranceCm);
			}
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Road gate: the road splines lie on the landscape.
	//
	// A spline actor existing says the GeoPackage was read; it does not say the roads stand on the
	// ground. Their heights are the file's, placed with no conversion, so a road layer stated in one
	// datum and cut in another (#278), or placed with the wrong origin, floats or sinks while every
	// count above stays green. Spline points are taken evenly across every placed road, and each is
	// measured against the nearest landscape vertex. The median is held to the placement gate's metre:
	// a draped centreline sits on the DEM the landscape is built from, while a datum or origin error
	// moves every point at once. The max is logged, not held, because a road on an embankment or a
	// bridge deck can stand off the 1 m grid by more than a metre without anything being misplaced.
	// ---------------------------------------------------------------------------------------------
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
			FLandscapeComponentDataInterface Data(Component, /*MipLevel*/ 0, /*WorkOnEditingLayer*/ false);
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

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
