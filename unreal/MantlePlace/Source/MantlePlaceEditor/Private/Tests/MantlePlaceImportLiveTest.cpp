// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest (base-bundle skip + layer presence)
#include "MantlePlaceVaultTypes.h"     // MantlePlaceMinSupportedManifestVersion (stale-fixture skip)
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceCoverageRasterLogic.h"  // IsCoverageRaster (which layers to expect)
#include "MantlePlaceCoverageRasterTypes.h"  // runtime: UMantlePlaceCoverageRasterData

#include "AssetRegistry/AssetRegistryModule.h"
#include "Editor.h"
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
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"

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
}

// Live end-to-end import against a real downloaded sample bundle. Runs in the editor
// (needs an editor world). Skips (with a warning) when the sample isn't present, so it
// stays portable. It intentionally leaves the imported actors in the level so the result
// can be inspected / screenshotted.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportLiveTest,
	"MantlePlace.Import.LiveFreeTier",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportLiveTest::RunTest(const FString& Parameters)
{
	// MP_LIVETEST_BUNDLE overrides; otherwise the in-tree live-test copy. No developer-machine
	// fallback paths — a missing bundle is a warn-and-pass skip (portable, no-op on CI).
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	FString Zip = FPlatformMisc::GetEnvironmentVariable(TEXT("MP_LIVETEST_BUNDLE"));
	if (Zip.IsEmpty() || !Platform.FileExists(*Zip))
	{
		Zip = FPaths::ProjectSavedDir() / TEXT("MantlePlace/livetest/download.zip");
	}
	if (!Platform.FileExists(*Zip))
	{
		AddWarning(FString::Printf(TEXT("Sample bundle not present (%s); skipping live import test."), *Zip));
		return true;
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
		if (bRead && !Manifest.bValid && Manifest.Version < MantlePlaceMinSupportedManifestVersion)
		{
			AddWarning(FString::Printf(
			    TEXT("Sample bundle is manifest v%d, below the v%d floor; skipping live import test. "
			         "Re-download this AOI from mantle.place/vault to re-cut it on the current pipeline."),
			    Manifest.Version, MantlePlaceMinSupportedManifestVersion));
			return true;
		}

		if (bRead && !Manifest.bValid && Manifest.DeliveryModel == TEXT("base_on_demand"))
		{
			AddWarning(TEXT("Base bundle — materialize incomplete, live import not exercised. "
			                "Generate its Unreal formats (vault panel or mantle.place/vault) and re-download."));
			return true;
		}
	}

	const FMantlePlaceImportResult Result =
		UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);

	AddInfo(FString::Printf(TEXT("jobId=%s actors=%d\n%s"),
		*Result.JobId, Result.CreatedActors.Num(), *Result.Message));

	TestTrue(TEXT("import reported success"), Result.bSuccess);
	TestTrue(TEXT("created at least 2 actors (landscape + mesh)"), Result.CreatedActors.Num() >= 2);

	// New-layer assertions fire only when the bundle actually ships the layer.
	if (Manifest.bHasRoadSplines)
	{
		int32 NumSplineActors = 0;
		for (const FString& Label : Result.CreatedActors)
		{
			if (Label.StartsWith(TEXT("MP_RoadSpline_")))
			{
				++NumSplineActors;
			}
		}
		TestTrue(TEXT("road-splines bundle produced at least one spline actor"), NumSplineActors >= 1);
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
		for (const FMantlePlaceLandscapeLayer& Layer : Manifest.LandscapeLayers)
		{
			if (FMantlePlaceCoverageRasterLogic::IsCoverageRaster(Layer.Name) && Layer.UeReady.Num() > 0)
			{
				ExpectedLayers.Add(Layer.Name);
			}
		}

		TMap<FString, UTexture2D*> ByLayer;
		if (ExpectedLayers.Num() > 0)
		{
			const FString CoveragePath =
				FString::Printf(TEXT("/Game/MantlePlace/%s/CoverageRasters"), *Manifest.JobId.Left(8));
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
	if (Landscape != nullptr && Landscape->LandscapeComponents.Num() > 0)
	{
		ULandscapeComponent* Component = Landscape->LandscapeComponents[0];
		UMaterialInstance* Instance = Component ? Component->GetMaterialInstance(0, /*InDynamic*/ false) : nullptr;
		UMaterial* Base = Instance ? Instance->GetMaterial() : nullptr;
		TestNotNull(TEXT("landscape component has a material instance"), Instance);
		TestTrue(TEXT("landscape component material is not the engine default surface"),
			Base != nullptr && Base != UMaterial::GetDefaultMaterial(MD_Surface));
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
	if (Landscape != nullptr && !Manifest.HeightmapPath.IsEmpty())
	{
		using namespace MantlePlaceImportLiveTest;
		constexpr int32 GridN = 8; // 64 probes: enough to discriminate, cheap to read back

		// --- the source heightmap, in map order (I north->south, J west->east) ---
		TArray<double> Source;
		if (IFileHandle* Handle = Platform.OpenRead(*Zip))
		{
			FZipArchiveReader Reader(Handle); // takes ownership of the handle
			TArray<uint8> Png;
			if (!Reader.TryReadFile(Manifest.HeightmapPath, Png))
			{
				AddError(FString::Printf(TEXT("Orientation gate: bundle has no entry \"%s\"."),
					*Manifest.HeightmapPath));
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
							const int32 Row = Manifest.bRow0IsNorth ? FromNorth : (Height - 1 - FromNorth);
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
			for (ULandscapeComponent* Component : Landscape->LandscapeComponents)
			{
				if (Component == nullptr)
				{
					continue;
				}
				FLandscapeComponentDataInterface Data(Component, /*MipLevel*/ 0, /*WorkOnEditingLayer*/ false);
				if (Data.GetRawHeightData() == nullptr)
				{
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
					TEXT("Orientation gate: only %d landscape vertices were readable."), Samples.Num()));
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

	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
