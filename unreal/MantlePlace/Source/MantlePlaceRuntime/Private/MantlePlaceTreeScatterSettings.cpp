// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTreeScatterSettings.h"

#include "MantlePlaceCoverageRasterTypes.h"
#include "MantlePlaceFoliageScatterLogic.h"

#include "PCGContext.h"
#include "PCGGraphExecutionStateInterface.h"
#include "Data/PCGBasePointData.h"
#include "Metadata/Accessors/PCGAttributeAccessorHelpers.h"
#include "Metadata/PCGAttributePropertySelector.h"
#include "Metadata/PCGMetadata.h"

#include "CollisionQueryParams.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(MantlePlaceTreeScatterSettings)

#define LOCTEXT_NAMESPACE "MantlePlaceTreeScatter"

namespace
{
using namespace MantlePlaceFoliageScatterLogic;

/**
 * One coverage raster's raw samples and its published mapping, from the texture's source data and
 * the UMantlePlaceCoverageRasterData the importer attached. False (an invalid raster, which filters
 * nothing) when the texture is absent, carries no mapping, or landed in a format the importer never
 * produces for a coverage raster.
 */
bool ReadRaster(const TSoftObjectPtr<UTexture2D>& Pointer, FRaster& Out, FString& OutWhy)
{
	if (Pointer.IsNull())
	{
		return false;
	}
	UTexture2D* Texture = Pointer.LoadSynchronous();
	if (Texture == nullptr)
	{
		OutWhy = FString::Printf(TEXT("%s does not load"), *Pointer.ToString());
		return false;
	}
	const UMantlePlaceCoverageRasterData* Data = Texture->GetAssetUserData<UMantlePlaceCoverageRasterData>();
	if (Data == nullptr)
	{
		OutWhy = FString::Printf(TEXT("%s carries no Mantle Place value mapping"), *Texture->GetName());
		return false;
	}
#if WITH_EDITORONLY_DATA
	TArray64<uint8> Bytes;
	if (!Texture->Source.IsValid() || !Texture->Source.GetMipData(Bytes, 0))
	{
		OutWhy = FString::Printf(TEXT("%s has no source data"), *Texture->GetName());
		return false;
	}
	const int32 Width = Texture->Source.GetSizeX();
	const int32 Height = Texture->Source.GetSizeY();
	const int64 Count = static_cast<int64>(Width) * Height;
	Out.Samples.SetNumUninitialized(static_cast<int32>(Count));
	switch (Texture->Source.GetFormat())
	{
	case TSF_G8:
		for (int64 Index = 0; Index < Count && Index < Bytes.Num(); ++Index)
		{
			Out.Samples[Index] = Bytes[Index];
		}
		Out.FullScale = 255.0;
		break;
	case TSF_G16:
		for (int64 Index = 0; Index < Count && Index * 2 + 1 < Bytes.Num(); ++Index)
		{
			Out.Samples[Index] = static_cast<uint16>(Bytes[Index * 2] | (Bytes[Index * 2 + 1] << 8));
		}
		Out.FullScale = 65535.0;
		break;
	case TSF_BGRA8:
		// An 8-bit raster the engine landed as colour: the value is the red channel (B, G, R, A).
		for (int64 Index = 0; Index < Count && Index * 4 + 2 < Bytes.Num(); ++Index)
		{
			Out.Samples[Index] = Bytes[Index * 4 + 2];
		}
		Out.FullScale = 255.0;
		break;
	default:
		OutWhy = FString::Printf(TEXT("%s is in a source format the scatter does not read"), *Texture->GetName());
		return false;
	}
	Out.Width = Width;
	Out.Height = Height;
	Out.Mapping = Data->Mapping;
	Out.MinValue = Data->MinValue;
	Out.MaxValue = Data->MaxValue;
	Out.bHasNodata = Data->bHasNodata;
	Out.NodataValue = Data->NodataValue;
	Out.TrueValue = Data->TrueValue;
	return Out.IsValid();
#else
	OutWhy = TEXT("raster source data exists in the editor only");
	return false;
#endif
}

template <typename T>
bool ReadColumn(const UPCGData* Data, const TCHAR* Column, TArray<T>& Out, FPCGContext* Context, bool bQuiet = false)
{
	const FPCGAttributePropertyInputSelector Selector =
		FPCGAttributePropertySelector::CreateAttributeSelector<FPCGAttributePropertyInputSelector>(FName(Column));
	return PCGAttributeAccessorHelpers::ExtractAllValues(Data, Selector, Out, Context,
		EPCGAttributeAccessorFlags::AllowBroadcastAndConstructible, bQuiet);
}

struct FMeshChoice
{
	FSoftObjectPath Path;
	FBox Bounds = FBox(ForceInit);
};

TArray<FMeshChoice> LoadMeshes(const TArray<TSoftObjectPtr<UStaticMesh>>& Pointers)
{
	TArray<FMeshChoice> Out;
	for (const TSoftObjectPtr<UStaticMesh>& Pointer : Pointers)
	{
		if (UStaticMesh* Mesh = Pointer.LoadSynchronous())
		{
			Out.Add(FMeshChoice{ Pointer.ToSoftObjectPath(), Mesh->GetBoundingBox() });
		}
	}
	return Out;
}
}

#if WITH_EDITOR
FText UMantlePlaceTreeScatterSettings::GetNodeTooltipText() const
{
	return LOCTEXT("NodeTooltip",
		"Scatters a Mantle Place bundle's tree points: binds the tree-points table's columns by name, drops points on "
		"water, on excluded land cover and under no canopy, and writes each survivor's mesh and transform.");
}
#endif

FPCGElementPtr UMantlePlaceTreeScatterSettings::CreateElement() const
{
	return MakeShared<FMantlePlaceTreeScatterElement>();
}

bool FMantlePlaceTreeScatterElement::ExecuteInternal(FPCGContext* Context) const
{
	check(Context);
	const UMantlePlaceTreeScatterSettings* Settings = Context->GetInputSettings<UMantlePlaceTreeScatterSettings>();
	check(Settings);

	FInputs Inputs;
	Inputs.AoiSizeCm = Settings->AoiSizeCm;
	FString Why;
	if (!ReadRaster(Settings->CanopyHeight, Inputs.CanopyHeight, Why) && !Why.IsEmpty())
	{
		PCGE_LOG(Warning, GraphAndLog, FText::Format(LOCTEXT("CanopyUnread", "Canopy height not applied: {0}."), FText::FromString(Why)));
	}
	Why.Reset();
	if (!ReadRaster(Settings->WaterMask, Inputs.WaterMask, Why) && !Why.IsEmpty())
	{
		PCGE_LOG(Warning, GraphAndLog, FText::Format(LOCTEXT("WaterUnread", "Water mask not applied: {0}."), FText::FromString(Why)));
	}
	Why.Reset();
	if (!ReadRaster(Settings->LandCover, Inputs.LandCover, Why) && !Why.IsEmpty())
	{
		PCGE_LOG(Warning, GraphAndLog, FText::Format(LOCTEXT("LandCoverUnread", "Land cover not applied: {0}."), FText::FromString(Why)));
	}

	FParams Params;
	Params.ExcludedLandCoverClasses = Settings->ExcludedLandCoverClasses;
	Params.MinCanopyHeightM = Settings->MinCanopyHeightM;
	Params.FullDensityCanopyHeightM = Settings->FullDensityCanopyHeightM;
	Params.DensityScale = Settings->DensityScale;
	Params.Seed = Settings->ScatterSeed;

	const TArray<FMeshChoice> TreeMeshes = LoadMeshes(Settings->TreeMeshes);
	const TArray<FMeshChoice> ShrubMeshes = Settings->ShrubMeshes.IsEmpty() ? TreeMeshes : LoadMeshes(Settings->ShrubMeshes);
	if (TreeMeshes.IsEmpty() && ShrubMeshes.IsEmpty())
	{
		PCGE_LOG(Error, GraphAndLog, LOCTEXT("NoMeshes", "No tree or shrub mesh loads: nothing to scatter."));
		return true;
	}

	// The cell this execution generates: on a partitioned component, one World Partition cell.
	const FBox Cell = Context->ExecutionSource.IsValid() ? Context->ExecutionSource->GetExecutionState().GetBounds() : FBox(ForceInit);
	const FBox* CellBounds = Cell.IsValid ? &Cell : nullptr;
	UWorld* World = Context->ExecutionSource.IsValid() ? Context->ExecutionSource->GetExecutionState().GetWorld() : nullptr;

	for (const FPCGTaggedData& Input : Context->InputData.GetInputsByPin(PCGPinConstants::DefaultInputLabel))
	{
		const UPCGBasePointData* Points = Cast<UPCGBasePointData>(Input.Data);
		if (Points == nullptr)
		{
			continue;
		}
		TArray<FVector> Positions;
		TArray<double> Heights, Crowns;
		TArray<bool> GroundKnown;
		TArray<int64> FoliageTypes;
		if (!ReadColumn(Points, MantlePlaceTreeScatterColumns::Position(), Positions, Context)
			|| !ReadColumn(Points, MantlePlaceTreeScatterColumns::HeightCm(), Heights, Context)
			|| !ReadColumn(Points, MantlePlaceTreeScatterColumns::CrownRadiusCm(), Crowns, Context)
			|| !ReadColumn(Points, MantlePlaceTreeScatterColumns::GroundKnown(), GroundKnown, Context))
		{
			PCGE_LOG(Error, GraphAndLog, LOCTEXT("ColumnsMissing", "The input is not the tree-points table: a contract column is missing."));
			continue;
		}
		// FoliageType is read where it binds; an input without it is all trees, as the contract says.
		if (!ReadColumn(Points, MantlePlaceTreeScatterColumns::FoliageType(), FoliageTypes, Context, /*bQuiet*/ true))
		{
			FoliageTypes.Init(0, Positions.Num());
		}

		UPCGBasePointData* Out = FPCGContext::NewPointData_AnyThread(Context);
		Out->SetNumPoints(Positions.Num(), /*bInitializeValues*/ true);
		Out->AllocateProperties(EPCGPointNativeProperties::Transform | EPCGPointNativeProperties::Seed | EPCGPointNativeProperties::MetadataEntry);
		FPCGMetadataAttribute<FSoftObjectPath>* MeshAttribute =
			Out->MutableMetadata()->FindOrCreateAttribute<FSoftObjectPath>(Settings->MeshAttribute, FSoftObjectPath(),
				/*bAllowsInterpolation*/ false, /*bOverrideParent*/ false);
		TPCGValueRange<FTransform> Transforms = Out->GetTransformValueRange();
		TPCGValueRange<int32> Seeds = Out->GetSeedValueRange();
		TPCGValueRange<PCGMetadataEntryKey> Entries = Out->GetMetadataEntryValueRange();

		int32 Written = 0;
		int32 NoGround = 0;
		for (int32 Index = 0; Index < Positions.Num(); ++Index)
		{
			FTreePoint Point;
			Point.PositionCm = Positions[Index];
			Point.HeightCm = static_cast<float>(Heights[Index]);
			Point.CrownRadiusCm = static_cast<float>(Crowns[Index]);
			Point.bGroundKnown = GroundKnown[Index];
			Point.bShrub = FoliageTypes.IsValidIndex(Index) && FoliageTypes[Index] == 1; // EMantlePlaceFoliageType::Shrub
			if (Judge(Point, Inputs, Params, CellBounds) != EVerdict::Keep)
			{
				continue;
			}
			const double HeightCm = TargetHeightCm(Point, Inputs);
			const TArray<FMeshChoice>& Meshes = (Point.bShrub && !ShrubMeshes.IsEmpty()) ? ShrubMeshes : TreeMeshes;
			const int32 Pick = PickMesh(Point.PositionCm, Meshes.Num(), Params.Seed);
			if (HeightCm <= 0.0 || Pick == INDEX_NONE)
			{
				continue;
			}

			// The published ground where it was measured; elsewhere the ground under the point, found
			// by a trace through the cell, since 0 is a real elevation and planting there is wrong.
			double GroundZ = Point.PositionCm.Z;
			if (!Point.bGroundKnown)
			{
				FHitResult Hit;
				const double Top = CellBounds != nullptr ? CellBounds->Max.Z : 1.0e6;
				const double Bottom = CellBounds != nullptr ? CellBounds->Min.Z : -1.0e6;
				if (World == nullptr || !World->LineTraceSingleByChannel(Hit,
					FVector(Point.PositionCm.X, Point.PositionCm.Y, Top),
					FVector(Point.PositionCm.X, Point.PositionCm.Y, Bottom), ECC_WorldStatic))
				{
					++NoGround;
					continue;
				}
				GroundZ = Hit.ImpactPoint.Z;
			}

			Transforms[Written] = InstanceTransform(Point, HeightCm, GroundZ, Meshes[Pick].Bounds, Params.Seed);
			Seeds[Written] = static_cast<int32>(Draw(Point.PositionCm, Params.Seed, 4) * MAX_int32);
			Out->MutableMetadata()->InitializeOnSet(Entries[Written]);
			MeshAttribute->SetValue(Entries[Written], Meshes[Pick].Path);
			++Written;
		}
		Out->SetNumPoints(Written);
		if (NoGround > 0)
		{
			PCGE_LOG(Warning, GraphAndLog, FText::Format(
				LOCTEXT("NoGround", "{0} tree point(s) with no measured ground and nothing under them were not planted."), NoGround));
		}
		Context->OutputData.TaggedData.Emplace_GetRef(Input).Data = Out;
	}
	return true;
}

#undef LOCTEXT_NAMESPACE
