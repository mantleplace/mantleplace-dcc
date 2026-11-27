// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceDataLayers.h"
#include "MantlePlaceFoliageScatterLogic.h"
#include "MantlePlaceFoliageVolume.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceLandcoverTypes.h"
#include "MantlePlaceTreePointsLogic.h"
#include "MantlePlaceTreeScatterSettings.h"

#include "Components/InstancedStaticMeshComponent.h"
#include "Containers/Ticker.h"
#include "DataLayer/DataLayerEditorSubsystem.h"
#include "Editor.h"
#include "Engine/DataTable.h"
#include "Engine/Level.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "Grid/PCGPartitionActor.h"
#include "HAL/FileManager.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "PCGComponent.h"
#include "Subsystems/PCGSubsystem.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "WorldPartition/DataLayer/DataLayerAsset.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionHelpers.h"

namespace MantlePlaceWorldPartitionFoliageTest
{
constexpr int32 RasterSize = 8;
constexpr double AoiCm = 40000.0;
const FString Identity = TEXT("ffffffff-6666");
const FString ContentRoot = TEXT("/Temp/MantlePlaceFoliageTest");

void SetAoi(FMantlePlaceVaultManifest& Manifest)
{
	Manifest.Heightmap.SectionSizeQuads = 100;
	Manifest.Heightmap.SectionsPerComponent = 1;
	Manifest.Heightmap.ComponentCountX = 4;
	Manifest.Heightmap.ComponentCountY = 4;
	Manifest.Heightmap.ScaleXPercent = 100.0;
	Manifest.Heightmap.ScaleYPercent = 100.0;
	Manifest.Heightmap.ZMinM = -10.0;
	Manifest.Heightmap.ZMaxM = 10.0;
	Manifest.Foliage.bPresent = true;
}

bool IsWaterTexel(int32 Column, int32 Row) { return (Row == 1 && Column >= 4) || Row == 5; }

UTexture2D* MakeRaster(UObject* Outer, const TCHAR* Name, ETextureSourceFormat Format, EMantlePlaceCoverageMapping Mapping,
	TFunctionRef<uint16(int32, int32)> Sample)
{
	UTexture2D* Texture = NewObject<UTexture2D>(Outer, MakeUniqueObjectName(Outer, UTexture2D::StaticClass(), FName(Name)), RF_Transient);
	const int32 BytesPerSample = Format == TSF_G16 ? 2 : 1;
	TArray<uint8> Bytes;
	Bytes.SetNumZeroed(RasterSize * RasterSize * BytesPerSample);
	for (int32 Row = 0; Row < RasterSize; ++Row)
	{
		for (int32 Column = 0; Column < RasterSize; ++Column)
		{
			const uint16 Value = Sample(Column, Row);
			const int32 At = (Row * RasterSize + Column) * BytesPerSample;
			Bytes[At] = static_cast<uint8>(Value & 0xFF);
			if (BytesPerSample == 2)
			{
				Bytes[At + 1] = static_cast<uint8>(Value >> 8);
			}
		}
	}
	Texture->Source.Init(RasterSize, RasterSize, 1, 1, Format, Bytes.GetData());
	UMantlePlaceCoverageRasterData* Data = NewObject<UMantlePlaceCoverageRasterData>(Texture);
	Data->Mapping = Mapping;
	Data->Orientation = EMantlePlaceCoverageOrientation::NorthUpRowMajor;
	Data->MinValue = 0.0;
	Data->MaxValue = 40.0;
	Data->TrueValue = 255.0;
	Data->FalseValue = 0.0;
	Texture->AddAssetUserData(Data);
	return Texture;
}

UDataTable* MakeTable(UObject* Outer)
{
	UDataTable* Table = NewObject<UDataTable>(Outer, MakeUniqueObjectName(Outer, UDataTable::StaticClass(), FName(TEXT("DT_TreePointsFoliageTest"))), RF_Transient);
	Table->RowStruct = FMantlePlaceTreePointRow::StaticStruct();
	int32 Index = 0;
	for (double North = -AoiCm / 2 + 1250.0; North < AoiCm / 2; North += 2500.0)
	{
		for (double East = -AoiCm / 2 + 1250.0; East < AoiCm / 2; East += 2500.0)
		{
			FMantlePlaceTreePointRow Row;
			Row.Position = FVector(North, East, 0.0);
			Row.FoliageType = EMantlePlaceFoliageType::Tree;
			Row.HeightM = 12.0f;
			Row.HeightCm = 1200.0f;
			Row.CrownRadiusM = 3.0f;
			Row.CrownRadiusCm = 300.0f;
			Row.bGroundKnown = true;
			Table->AddRow(FName(*FString::Printf(TEXT("Tree_%d"), Index++)), Row);
		}
	}
	return Table;
}

MantlePlaceFoliageScatterLogic::FRaster WaterRaster()
{
	MantlePlaceFoliageScatterLogic::FRaster Raster;
	Raster.Width = RasterSize;
	Raster.Height = RasterSize;
	Raster.Mapping = EMantlePlaceCoverageMapping::Mask;
	Raster.TrueValue = 255.0;
	Raster.FullScale = 255.0;
	for (int32 Row = 0; Row < RasterSize; ++Row)
	{
		for (int32 Column = 0; Column < RasterSize; ++Column)
		{
			Raster.Samples.Add(IsWaterTexel(Column, Row) ? 255 : 0);
		}
	}
	return Raster;
}

bool PumpUntilGenerated(UWorld* World, UPCGComponent* Component)
{
	UPCGSubsystem* Subsystem = UPCGSubsystem::GetInstance(World);
	for (int32 Iteration = 0; Iteration < 2000; ++Iteration)
	{
		FTSTicker::GetCoreTicker().Tick(1.0f / 30.0f);
		if (Subsystem != nullptr)
		{
			Subsystem->Tick(1.0f / 30.0f);
		}
		bool bBusy = Component->IsGenerating();
		for (TActorIterator<APCGPartitionActor> It(World); It && !bBusy; ++It)
		{
			for (const TObjectPtr<UPCGComponent>& Local : It->GetAllLocalPCGComponents())
			{
				bBusy |= Local != nullptr && Local->IsGenerating();
			}
		}
		if (!bBusy && Iteration > 2)
		{
			return true;
		}
	}
	return false;
}

TArray<FVector> InstanceLocations(UWorld* World)
{
	TArray<FVector> Out;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		TArray<UInstancedStaticMeshComponent*> Components;
		It->GetComponents(Components);
		for (const UInstancedStaticMeshComponent* Component : Components)
		{
			for (int32 Index = 0; Index < Component->GetInstanceCount(); ++Index)
			{
				FTransform Transform;
				Component->GetInstanceTransform(Index, Transform, true);
				Out.Add(Transform.GetLocation());
			}
		}
	}
	return Out;
}

int32 CountPartitionActors(UWorld* World)
{
	int32 Count = 0;
	for (TActorIterator<APCGPartitionActor> It(World); It; ++It)
	{
		++Count;
	}
	return Count;
}

int32 CountTagged(UWorld* World)
{
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += It->Tags.Contains(Tag) ? 1 : 0;
	}
	return Count;
}
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliageScatter();
}

namespace MantlePlaceWorldPartitionFoliageTest
{
AMantlePlaceFoliageVolume* ImportScatter(UWorld* World, const FString& DestPackagePath, TArray<FString>& Log)
{
	UPackage* Outer = GetTransientPackage();
	FMantlePlaceVaultManifest Manifest;
	SetAoi(Manifest);
	FMantlePlaceImportResult Result;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	const FMantlePlaceImportSelection Selection = FMantlePlaceImportSelection::FromMode(EMantlePlaceImportMode::Landscape, true);
	FMantlePlaceImportContext Context(World, Manifest, nullptr, Selection, Timeline, Result, Log);
	Context.Identity = Identity;
	Context.ContentRoot = ContentRoot;
	Context.DestPackagePath = DestPackagePath;
	Context.Family = EMantlePlaceLayerFamily::Foliage;
	Context.TreePointsTable = MakeTable(Outer);
	Context.CoverageTextures.Add(TEXT("water_mask"), MakeRaster(Outer, TEXT("T_WaterMaskFoliageTest"), TSF_G8,
		EMantlePlaceCoverageMapping::Mask, [](int32 Column, int32 Row) -> uint16 { return IsWaterTexel(Column, Row) ? 255 : 0; }));
	Context.CoverageTextures.Add(TEXT("worldcover"), MakeRaster(Outer, TEXT("T_WorldCoverFoliageTest"), TSF_G8,
		EMantlePlaceCoverageMapping::Legend, [](int32 Column, int32 Row) -> uint16 { return (Column == 0 && Row == 0) ? 50 : 10; }));
	Context.CoverageTextures.Add(TEXT("canopy_height"), MakeRaster(Outer, TEXT("T_CanopyHeightFoliageTest"), TSF_G16,
		EMantlePlaceCoverageMapping::Scale, [](int32, int32) -> uint16 { return 40000; }));

	TUniquePtr<IMantlePlaceArtifactHandler> Handler = MantlePlaceArtifacts::MakeFoliageScatter();
	Handler->Import(Context);
	AMantlePlaceFoliageVolume* Volume = nullptr;
	for (TActorIterator<AMantlePlaceFoliageVolume> It(World); It; ++It)
	{
		Volume = *It;
	}
	return Volume;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFoliageScatterColumnsTest,
	"MantlePlace.Import.FoliageScatter.Columns",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceFoliageScatterColumnsTest::RunTest(const FString& Parameters)
{
	const TArray<FName>& Contract = FMantlePlaceTreePointsLogic::ColumnContract();
	for (const TCHAR* Column : { MantlePlaceTreeScatterColumns::Position(), MantlePlaceTreeScatterColumns::FoliageType(),
			 MantlePlaceTreeScatterColumns::HeightCm(), MantlePlaceTreeScatterColumns::CrownRadiusCm(),
			 MantlePlaceTreeScatterColumns::GroundKnown() })
	{
		TestTrue(FString::Printf(TEXT("the scatter's column %s is in the contract"), Column), Contract.Contains(FName(Column)));
	}

	for (const EMantlePlaceImportMode Mode : { EMantlePlaceImportMode::Landscape, EMantlePlaceImportMode::Mesh, EMantlePlaceImportMode::Both })
	{
		TestFalse(TEXT("FromMode leaves the scatter out by default"),
			FMantlePlaceImportSelection::FromMode(Mode).Includes(MantlePlaceArtifactId::FoliageScatter()));
		TestTrue(TEXT("FromMode includes the scatter when opted in"),
			FMantlePlaceImportSelection::FromMode(Mode, true).Includes(MantlePlaceArtifactId::FoliageScatter()));
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionFoliageTest,
	"MantlePlace.Import.WorldPartition.FoliageScatter",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionFoliageTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionFoliageTest;

	if (GEditor == nullptr || UDataLayerEditorSubsystem::Get() == nullptr)
	{
		AddInfo(TEXT("No editor: the foliage round trip needs an editor world. Skipped."));
		return true;
	}

	for (const bool bPartitioned : { true, false })
	{
		const FString Pass = bPartitioned ? TEXT("partitioned") : TEXT("non-partitioned");
		UWorld::InitializationValues IVS;
		IVS.CreateWorldPartition(bPartitioned).EnableWorldPartitionStreaming(bPartitioned);
		UWorld* World = UWorld::CreateWorld(EWorldType::Editor, false,
			FName(*FString::Printf(TEXT("MantlePlaceFoliageTestWorld_%s"), bPartitioned ? TEXT("WP") : TEXT("Flat"))),
			nullptr, true, ERHIFeatureLevel::Num, &IVS);
		if (!TestNotNull(*FString::Printf(TEXT("%s: a throwaway world"), *Pass), World))
		{
			return false;
		}

		TArray<FString> Log;
		MantlePlaceImportTiming::FTimeline Timeline;
		Timeline.Start();
		AMantlePlaceFoliageVolume* Volume = ImportScatter(World, MantlePlaceImportNaming::ImportRoot(ContentRoot, Identity), Log);
		if (!TestNotNull(*FString::Printf(TEXT("%s: the scatter spawns its volume (log: %s)"), *Pass, *FString::Join(Log, TEXT(" | "))), Volume))
		{
			World->DestroyWorld(false);
			continue;
		}
		TestEqual(*FString::Printf(TEXT("%s: the component is partitioned exactly when the level is"), *Pass),
			Volume->Pcg->IsPartitioned(), bPartitioned);
		TestTrue(*FString::Printf(TEXT("%s: generation finishes"), *Pass), PumpUntilGenerated(World, Volume->Pcg));

		const TArray<FVector> Instances = InstanceLocations(World);
		TestTrue(*FString::Printf(TEXT("%s: the scatter produced instances (%d)"), *Pass, Instances.Num()), Instances.Num() > 0);

		if (bPartitioned)
		{
			TestTrue(*FString::Printf(TEXT("%s: the AOI spans more than one cell (%d partition actors)"), *Pass, CountPartitionActors(World)),
				CountPartitionActors(World) > 1);
			const UDataLayerAsset* Foliage = MantlePlaceDataLayers::FindAsset(ContentRoot, EMantlePlaceLayerFamily::Foliage);
			TestTrue(*FString::Printf(TEXT("%s: the volume is in the foliage Data Layer"), *Pass),
				Foliage != nullptr && Volume->GetDataLayerAssets().Contains(Foliage));
			int32 OutOfLayer = 0;
			for (TActorIterator<APCGPartitionActor> It(World); It; ++It)
			{
				OutOfLayer += It->GetDataLayerAssets().Contains(Foliage) ? 0 : 1;
			}
			TestEqual(*FString::Printf(TEXT("%s: every partition actor is in the foliage Data Layer"), *Pass), OutOfLayer, 0);
		}
		else
		{
			TestEqual(*FString::Printf(TEXT("%s: no partition actor in a non-partitioned level"), *Pass), CountPartitionActors(World), 0);
		}

		const MantlePlaceFoliageScatterLogic::FRaster WaterMask = WaterRaster();
		const FVector2D Aoi(AoiCm, AoiCm);
		int32 OutsideAoi = 0, OnWater = 0;
		for (const FVector& Location : Instances)
		{
			OutsideAoi += (FMath::Abs(Location.X) > AoiCm / 2 || FMath::Abs(Location.Y) > AoiCm / 2) ? 1 : 0;
			uint16 Sample = 0;
			OnWater += (MantlePlaceFoliageScatterLogic::RawAt(WaterMask, Aoi, FVector(Location.X, Location.Y, 0.0), Sample) && Sample == 255) ? 1 : 0;
		}
		TestEqual(*FString::Printf(TEXT("%s: every instance is inside the AOI"), *Pass), OutsideAoi, 0);
		TestEqual(*FString::Printf(TEXT("%s: zero instances on water-classified texels"), *Pass), OnWater, 0);
		const double Texel = AoiCm / RasterSize;
		int32 WaterTexels = 0, InstancesInWaterTexels = 0;
		for (int32 Row = 0; Row < RasterSize; ++Row)
		{
			for (int32 Column = 0; Column < RasterSize; ++Column)
			{
				if (!IsWaterTexel(Column, Row))
				{
					continue;
				}
				++WaterTexels;
				const FVector2D Centre(AoiCm / 2 - (Row + 0.5) * Texel, -AoiCm / 2 + (Column + 0.5) * Texel);
				for (const FVector& Location : Instances)
				{
					InstancesInWaterTexels += (FMath::Abs(Location.X - Centre.X) < Texel / 2 && FMath::Abs(Location.Y - Centre.Y) < Texel / 2) ? 1 : 0;
				}
			}
		}
		TestTrue(*FString::Printf(TEXT("%s: the fixture has water texels the table's points stand on"), *Pass), WaterTexels > 0);
		TestEqual(*FString::Printf(TEXT("%s: zero instances at the water texels' sample points"), *Pass), InstancesInWaterTexels, 0);
		TestEqual(*FString::Printf(TEXT("%s: every point off water is scattered, built-up land included"), *Pass), Instances.Num(), 256 - (4 + 8) * 4);

		MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline);
		TestEqual(*FString::Printf(TEXT("%s: the wipe leaves no tagged actor"), *Pass), CountTagged(World), 0);
		TestEqual(*FString::Printf(TEXT("%s: the wipe leaves no partition actor"), *Pass), CountPartitionActors(World), 0);
		TestEqual(*FString::Printf(TEXT("%s: the wipe leaves no instance"), *Pass), InstanceLocations(World).Num(), 0);

		World->DestroyWorld(false);
	}
	return true;
}

namespace MantlePlaceWorldPartitionFoliageTest
{
int32 SavePartitionActors(UWorld* World)
{
	int32 Saved = 0;
	for (TActorIterator<APCGPartitionActor> It(World); It; ++It)
	{
		UPackage* Package = It->GetExternalPackage();
		if (Package == nullptr)
		{
			continue;
		}
		FSavePackageArgs Args;
		Args.TopLevelFlags = RF_Standalone;
		Args.SaveFlags = SAVE_NoError;
		const FString Filename = FPackageName::LongPackageNameToFilename(Package->GetName(), FPackageName::GetAssetPackageExtension());
		Saved += UPackage::SavePackage(Package, nullptr, *Filename, Args) ? 1 : 0;
	}
	return Saved;
}

int32 CountPartitionDescriptors(UWorld* World)
{
	int32 Count = 0;
	if (UWorldPartition* WorldPartition = World->GetWorldPartition())
	{
		FWorldPartitionHelpers::ForEachActorDescInstance<APCGPartitionActor>(WorldPartition,
			[&Count](const FWorldPartitionActorDescInstance*) { ++Count; return true; });
	}
	return Count;
}

int32 CountPartitionActorsWithout(UWorld* World, UPCGComponent* Component)
{
	int32 Count = 0;
	for (TActorIterator<APCGPartitionActor> It(World); It; ++It)
	{
		Count += It->GetLocalComponent(Component) != nullptr ? 0 : 1;
	}
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceWorldPartitionFoliageReimportTest,
	"MantlePlace.Import.WorldPartition.FoliageScatterReimport",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceWorldPartitionFoliageReimportTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceWorldPartitionFoliageTest;

	if (GEditor == nullptr || UDataLayerEditorSubsystem::Get() == nullptr)
	{
		AddInfo(TEXT("No editor: the foliage re-import needs an editor world. Skipped."));
		return true;
	}

	const FString LevelPackageName = TEXT("/Temp/MantlePlaceFoliageReimportTest");
	const FString SavedActorsDir = FPaths::ConvertRelativePathToFull(
		FPackageName::LongPackageNameToFilename(ULevel::GetExternalActorsPath(LevelPackageName)));
	IFileManager::Get().DeleteDirectory(*SavedActorsDir, false, true);

	UWorld::InitializationValues IVS;
	IVS.CreateWorldPartition(true).EnableWorldPartitionStreaming(true);
	UWorld* World = UWorld::CreateWorld(EWorldType::Editor, false, FName(TEXT("MantlePlaceFoliageReimportTest")),
		CreatePackage(*LevelPackageName), true, ERHIFeatureLevel::Num, &IVS);
	if (!TestNotNull(TEXT("a throwaway partitioned world with a package"), World))
	{
		return false;
	}

	TArray<FString> Log;
	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	AMantlePlaceFoliageVolume* First = ImportScatter(World, ContentRoot / TEXT("ReimportFirst"), Log);
	if (!TestNotNull(*FString::Printf(TEXT("the first import spawns its volume (log: %s)"), *FString::Join(Log, TEXT(" | "))), First))
	{
		World->DestroyWorld(false);
		return false;
	}
	TestTrue(TEXT("the first import's generation finishes"), PumpUntilGenerated(World, First->Pcg));
	const int32 FirstPartitionActors = CountPartitionActors(World);
	const int32 FirstInstances = InstanceLocations(World).Num();
	TestTrue(*FString::Printf(TEXT("the first import spans more than one cell (%d partition actors)"), FirstPartitionActors), FirstPartitionActors > 1);
	TestTrue(*FString::Printf(TEXT("the first import scatters instances (%d)"), FirstInstances), FirstInstances > 0);

	TestEqual(TEXT("every partition actor is saved"), SavePartitionActors(World), FirstPartitionActors);
	TestEqual(TEXT("the saved level describes every partition actor"), CountPartitionDescriptors(World), FirstPartitionActors);

	TArray<TWeakObjectPtr<AActor>> Kept;
	MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline, &Kept);
	AMantlePlaceFoliageVolume* Second = ImportScatter(World, ContentRoot / TEXT("ReimportSecond"), Log);
	MantlePlaceImportOrchestration::ReleaseKeptPartitionActors(World, Kept);
	if (!TestNotNull(TEXT("the re-import spawns its volume"), Second))
	{
		World->DestroyWorld(false);
		IFileManager::Get().DeleteDirectory(*SavedActorsDir, false, true);
		return false;
	}
	TestTrue(TEXT("the re-import's generation finishes"), PumpUntilGenerated(World, Second->Pcg));
	TestEqual(TEXT("the re-import leaves one volume, its own"), CountTagged(World), 1);
	TestEqual(TEXT("the re-import regenerates on as many partition actors as the first import"), CountPartitionActors(World), FirstPartitionActors);
	TestEqual(TEXT("the re-import regenerates as many instances as the first import"), InstanceLocations(World).Num(), FirstInstances);
	TestEqual(TEXT("every partition actor holds the re-import's scatter: no leftover, no cell missed"),
		CountPartitionActorsWithout(World, Second->Pcg), 0);
	const UDataLayerAsset* Foliage = MantlePlaceDataLayers::FindAsset(ContentRoot, EMantlePlaceLayerFamily::Foliage);
	int32 OutOfLayer = 0;
	for (TActorIterator<APCGPartitionActor> It(World); It; ++It)
	{
		OutOfLayer += It->GetDataLayerAssets().Contains(Foliage) ? 0 : 1;
	}
	TestEqual(TEXT("every partition actor is still in the foliage Data Layer"), OutOfLayer, 0);

	TestEqual(TEXT("every partition actor saves again"), SavePartitionActors(World), FirstPartitionActors);
	PumpUntilGenerated(World, Second->Pcg);
	TestEqual(TEXT("after the save, the level describes one scatter's partition actors"), CountPartitionDescriptors(World), FirstPartitionActors);
	TestEqual(TEXT("after the save, the partition actors are unchanged"), CountPartitionActors(World), FirstPartitionActors);
	TestEqual(TEXT("after the save, the instances are unchanged"), InstanceLocations(World).Num(), FirstInstances);

	TArray<TWeakObjectPtr<AActor>> KeptAgain;
	MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline, &KeptAgain);
	TestEqual(TEXT("the wipe keeps every emptied partition actor for the import that follows"), KeptAgain.Num(), FirstPartitionActors);
	MantlePlaceImportOrchestration::ReleaseKeptPartitionActors(World, KeptAgain);
	TestEqual(TEXT("the wipe leaves no tagged actor"), CountTagged(World), 0);
	TestEqual(TEXT("the wipe leaves no partition actor"), CountPartitionActors(World), 0);
	TestEqual(TEXT("the wipe leaves no instance"), InstanceLocations(World).Num(), 0);

	World->DestroyWorld(false);
	IFileManager::Get().DeleteDirectory(*SavedActorsDir, false, true);
	return true;
}

#endif
