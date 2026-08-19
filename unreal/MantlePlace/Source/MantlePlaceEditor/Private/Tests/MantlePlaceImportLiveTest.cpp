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
#include "HAL/PlatformFileManager.h"
#include "Landscape.h"
#include "LandscapeComponent.h"
#include "LandscapeInfo.h"
#include "LandscapeLayerInfoObject.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstance.h"
#include "Misc/Paths.h"

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

	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
