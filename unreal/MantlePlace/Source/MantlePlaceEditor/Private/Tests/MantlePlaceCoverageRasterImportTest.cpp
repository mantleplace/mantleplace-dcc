// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCoverageRasters.h"
#include "MantlePlaceCoverageRasterTypes.h"
#include "MantlePlaceImportManifest.h"

#include "Editor.h"
#include "Engine/Texture2D.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Subsystems/EditorAssetSubsystem.h"

namespace
{
constexpr int32 TestWidth = 143;
constexpr int32 TestHeight = 142;

TArray<uint16> MakeSamples()
{
	TArray<uint16> Samples;
	Samples.SetNumUninitialized(TestWidth * TestHeight);
	for (int32 Index = 0; Index < Samples.Num(); ++Index)
	{
		Samples[Index] = static_cast<uint16>((Index * 517) % 65536);
	}
	return Samples;
}

bool WriteGray16Png(const FString& File, const TArray<uint16>& Samples)
{
	IImageWrapperModule& Module = FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
	const TSharedPtr<IImageWrapper> Wrapper = Module.CreateImageWrapper(EImageFormat::PNG);
	if (!Wrapper.IsValid())
	{
		return false;
	}
	if (!Wrapper->SetRaw(Samples.GetData(), Samples.Num() * sizeof(uint16), TestWidth, TestHeight, ERGBFormat::Gray, 16))
	{
		return false;
	}
	const TArray64<uint8> Compressed = Wrapper->GetCompressed();
	return FFileHelper::SaveArrayToFile(Compressed, *File);
}

FMantlePlaceLandscapeLayer MakeSlopeLayer(const FString& InZipPath)
{
	FMantlePlaceUeReadyRaster Raster;
	Raster.Path = InZipPath;
	Raster.Sha256 = TEXT("0000000000000000000000000000000000000000000000000000000000000000");
	Raster.Encoding = TEXT("png-16bit-grayscale");
	Raster.Width = TestWidth;
	Raster.Height = TestHeight;
	Raster.ValueMapping.MinValue = 0.0;
	Raster.ValueMapping.MaxValue = 90.0;
	Raster.ValueMapping.ToValueFormula = TEXT("value = min + (u16/65535)*(max - min)");
	Raster.ValueMapping.Units = TEXT("degrees");
	Raster.ValueMapping.bHasNodata = true;
	Raster.ValueMapping.NodataValue = 0.0;

	FMantlePlaceLandscapeLayer Layer;
	Layer.Name = TEXT("slope");
	Layer.Path = TEXT("Elevation/Slope.tif");
	Layer.UeReady = { Raster };
	return Layer;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCoverageRasterImportTest,
    "MantlePlace.Import.CoverageRasterImport",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCoverageRasterImportTest::RunTest(const FString& Parameters)
{
	const FString DestPackagePath = TEXT("/Game/MantlePlace/0badf00d");
	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("CoverageRasterTest");
	const FString PngFile = TempDir / TEXT("Slope.png");

	const TArray<uint16> Samples = MakeSamples();
	if (!WriteGray16Png(PngFile, Samples))
	{
		AddError(TEXT("Could not write the 16-bit grayscale PNG fixture."));
		return false;
	}

	const FMantlePlaceLandscapeLayer Layer = MakeSlopeLayer(TEXT("Elevation/Slope.png"));
	FString Error;
	UTexture2D* Texture = MantlePlaceCoverageRasters::ImportRaster(
		Layer, Layer.UeReady[0], PngFile, TEXT("0badf00d-cafe"), DestPackagePath, Error);

	TestNotNull(*FString::Printf(TEXT("the raster imported (%s)"), *Error), Texture);
	if (Texture != nullptr)
	{
		TestEqual(TEXT("a 16-bit grayscale PNG lands as TSF_G16"), Texture->Source.GetFormat(), TSF_G16);

		TestEqual(TEXT("TC_Grayscale (-> PF_G16)"), Texture->CompressionSettings.GetValue(), TC_Grayscale);
		TestFalse(TEXT("not sRGB"), Texture->SRGB != 0);
		TestEqual(TEXT("no mips"), Texture->MipGenSettings.GetValue(), TMGS_NoMipmaps);
		TestFalse(TEXT("not virtual-textured"), Texture->VirtualTextureStreaming != 0);
		TestEqual(TEXT("clamped, not wrapped"), Texture->AddressX.GetValue(), TA_Clamp);
		TestEqual(TEXT("width survives"), static_cast<int32>(Texture->Source.GetSizeX()), TestWidth);
		TestEqual(TEXT("height survives"), static_cast<int32>(Texture->Source.GetSizeY()), TestHeight);

		const UMantlePlaceCoverageRasterData* Data = Cast<UMantlePlaceCoverageRasterData>(
			Texture->GetAssetUserDataOfClass(UMantlePlaceCoverageRasterData::StaticClass()));
		TestNotNull(TEXT("the coverage-raster payload is attached"), Data);
		if (Data != nullptr)
		{
			TestEqual(TEXT("a 16-bit ramp is a Scale"), Data->Mapping, EMantlePlaceCoverageMapping::Scale);
			TestEqual(TEXT("units survive"), Data->Units, FString(TEXT("degrees")));
			TestEqual(TEXT("layer name survives"), Data->LayerName, FString(TEXT("slope")));
			TestEqual(TEXT("max survives"), Data->MaxValue, 90.0);
			TestTrue(TEXT("nodata is flagged"), Data->bHasNodata);
			TestEqual(TEXT("provenance records the in-zip path"), Data->SourcePath, FString(TEXT("Elevation/Slope.png")));
			TestEqual(TEXT("provenance records the job"), Data->JobId, FString(TEXT("0badf00d-cafe")));
			TestFalse(TEXT("provenance records the declared digest"), Data->Sha256.IsEmpty());
		}

		TArray64<uint8> Mip;
		if (Texture->Source.GetMipData(Mip, 0) && Mip.Num() == Samples.Num() * 2)
		{
			const uint16* Landed = reinterpret_cast<const uint16*>(Mip.GetData());
			int32 FirstMismatch = INDEX_NONE;
			for (int32 Index = 0; Index < Samples.Num(); ++Index)
			{
				if (Landed[Index] != Samples[Index])
				{
					FirstMismatch = Index;
					break;
				}
			}
			TestEqual(TEXT("every 16-bit sample round-trips unchanged"), FirstMismatch, INDEX_NONE);
			if (FirstMismatch != INDEX_NONE)
			{
				AddError(FString::Printf(TEXT("sample %d: wrote %u, read back %u"),
					FirstMismatch, Samples[FirstMismatch], Landed[FirstMismatch]));
			}
		}
		else
		{
			AddError(FString::Printf(TEXT("Source mip 0 is %lld bytes; expected %d."),
				Mip.Num(), Samples.Num() * 2));
		}
	}

	{
		FMantlePlaceLandscapeLayer Mismatched = MakeSlopeLayer(TEXT("Elevation/Aspect.png"));
		Mismatched.Name = TEXT("aspect");

		IImageWrapperModule& Module = FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
		const TSharedPtr<IImageWrapper> Wrapper = Module.CreateImageWrapper(EImageFormat::PNG);
		TArray<uint8> Bytes;
		Bytes.SetNumZeroed(TestWidth * TestHeight);
		for (int32 Index = 0; Index < Bytes.Num(); ++Index)
		{
			Bytes[Index] = static_cast<uint8>(Index % 251);
		}
		const FString EightBitFile = TempDir / TEXT("Aspect.png");
		if (Wrapper.IsValid()
			&& Wrapper->SetRaw(Bytes.GetData(), Bytes.Num(), TestWidth, TestHeight, ERGBFormat::Gray, 8))
		{
			FFileHelper::SaveArrayToFile(Wrapper->GetCompressed(), *EightBitFile);

			FString MismatchError;
			UTexture2D* Refused = MantlePlaceCoverageRasters::ImportRaster(
				Mismatched, Mismatched.UeReady[0], EightBitFile, TEXT("0badf00d-cafe"), DestPackagePath, MismatchError);
			TestNull(TEXT("an 8-bit PNG declared as 16-bit is refused"), Refused);
			TestTrue(TEXT("and the refusal names the declared encoding"),
				MismatchError.Contains(TEXT("png-16bit-grayscale")));
		}
	}

	if (UEditorAssetSubsystem* AssetSubsystem = GEditor ? GEditor->GetEditorSubsystem<UEditorAssetSubsystem>() : nullptr)
	{
		if (AssetSubsystem->DoesDirectoryExist(DestPackagePath))
		{
			AssetSubsystem->DeleteDirectory(DestPackagePath);
		}
	}
	IFileManager::Get().DeleteDirectory(*TempDir, false, true);

	return true;
}

#endif
