// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLevelExtent.h"

#include "Editor.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "LandscapeProxy.h"
#include "MantlePlaceExtentBox.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "ScopedTransaction.h"
#include "UObject/UnrealType.h"
#include "WorldPartition/WorldPartition.h"

#define LOCTEXT_NAMESPACE "MantlePlaceLevelExtent"

namespace
{
	/** The first actor of the class named by `ClassPath` in the world; null when the class is not loaded. */
	AActor* FindActorOfClassPath(UWorld* World, const TCHAR* ClassPath)
	{
		if (World == nullptr)
		{
			return nullptr;
		}
		// Looked up, never loaded: a plugin whose module is not loaded has no class here, and that is
		// the whole of "this georeference is absent".
		const UClass* Class = FindObject<UClass>(nullptr, ClassPath);
		if (Class == nullptr)
		{
			return nullptr;
		}
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			if (It->IsA(Class))
			{
				return *It;
			}
		}
		return nullptr;
	}

	/**
	 * Call a native function by reflection. AActor::ProcessEvent silently does nothing until the world's
	 * actors are initialized (a level still loading, a throwaway world), and both georeferences' converters
	 * are plain native C++ with no script to guard, so the call goes to UObject's.
	 */
	void CallNative(AActor* Actor, const UFunction* Function, void* Params)
	{
		Actor->UObject::ProcessEvent(const_cast<UFunction*>(Function), Params);
	}

	FStructProperty* FindVectorProperty(const UFunction* Function, const TCHAR* Name)
	{
		FStructProperty* Property = CastField<FStructProperty>(Function->FindPropertyByName(Name));
		return (Property != nullptr && Property->Struct == TBaseStructure<FVector>::Get()) ? Property : nullptr;
	}

	double ReadDoubleField(const FStructProperty* Struct, const void* StructValue, const TCHAR* Name, bool& bOk)
	{
		const FDoubleProperty* Field = CastField<FDoubleProperty>(Struct->Struct->FindPropertyByName(Name));
		if (Field == nullptr)
		{
			bOk = false;
			return 0.0;
		}
		return Field->GetPropertyValue_InContainer(StructValue);
	}

	/**
	 * Epic's GeoReferencing: `void EngineToGeographic(const FVector& EngineCoordinates,
	 * FGeographicCoordinates& GeographicCoordinates)`, the result a struct with Longitude and Latitude.
	 */
	bool ConvertNative(AActor* Actor, const FVector& Engine, FMantlePlaceGeoPoint& OutPoint)
	{
		const UFunction* Function = Actor->FindFunction(TEXT("EngineToGeographic"));
		if (Function == nullptr)
		{
			return false;
		}
		FStructProperty* In = FindVectorProperty(Function, TEXT("EngineCoordinates"));
		FStructProperty* Out = CastField<FStructProperty>(Function->FindPropertyByName(TEXT("GeographicCoordinates")));
		if (In == nullptr || Out == nullptr)
		{
			return false;
		}
		FStructOnScope Params(Function);
		*In->ContainerPtrToValuePtr<FVector>(Params.GetStructMemory()) = Engine;
		CallNative(Actor, Function, Params.GetStructMemory());

		bool bOk = true;
		const void* Result = Out->ContainerPtrToValuePtr<void>(Params.GetStructMemory());
		OutPoint.Longitude = ReadDoubleField(Out, Result, TEXT("Longitude"), bOk);
		OutPoint.Latitude = ReadDoubleField(Out, Result, TEXT("Latitude"), bOk);
		return bOk;
	}

	/**
	 * Cesium for Unreal: `FVector TransformUnrealPositionToLongitudeLatitudeHeight(const FVector&
	 * UnrealPosition) const`, the result (longitude, latitude, height) in degrees and metres.
	 */
	bool ConvertCesium(AActor* Actor, const FVector& Engine, FMantlePlaceGeoPoint& OutPoint)
	{
		const UFunction* Function = Actor->FindFunction(TEXT("TransformUnrealPositionToLongitudeLatitudeHeight"));
		if (Function == nullptr)
		{
			return false;
		}
		FStructProperty* In = FindVectorProperty(Function, TEXT("UnrealPosition"));
		FStructProperty* Out = FindVectorProperty(Function, TEXT("ReturnValue"));
		if (In == nullptr || Out == nullptr)
		{
			return false;
		}
		FStructOnScope Params(Function);
		// Cesium reads a position in the georeference actor's own frame ("a parent Actor's reference
		// frame"), which is the world frame only while the actor sits at the origin untransformed.
		*In->ContainerPtrToValuePtr<FVector>(Params.GetStructMemory()) = Actor->GetActorTransform().InverseTransformPosition(Engine);
		CallNative(Actor, Function, Params.GetStructMemory());

		const FVector Result = *Out->ContainerPtrToValuePtr<FVector>(Params.GetStructMemory());
		OutPoint.Longitude = Result.X;
		OutPoint.Latitude = Result.Y;
		return true;
	}

	/** Select one actor so Details shows it. */
	void Select(AActor* Actor)
	{
		// Only the editor's own world has a selection to put it in.
		if (GEditor != nullptr && Actor->GetWorld() == GEditor->GetEditorWorldContext().World())
		{
			GEditor->SelectNone(/*bNoteSelectionChange*/ false, /*bDeselectBSPSurfs*/ true);
			GEditor->SelectActor(Actor, /*bInSelected*/ true, /*bNotify*/ true);
		}
	}

	/** The tag value after a prefix, or false. Same shape as the handover's reader. */
	bool TagValue(const AActor& Actor, const FString& Prefix, FString& OutValue)
	{
		for (const FName& Tag : Actor.Tags)
		{
			const FString Text = Tag.ToString();
			if (Text.StartsWith(Prefix))
			{
				OutValue = Text.Mid(Prefix.Len());
				return true;
			}
		}
		return false;
	}
}

namespace MantlePlaceLevelExtent
{
	const TCHAR* NativeGeoreferenceClassPath()
	{
		return TEXT("/Script/GeoReferencing.GeoReferencingSystem");
	}

	const TCHAR* CesiumGeoreferenceClassPath()
	{
		return TEXT("/Script/CesiumRuntime.CesiumGeoreference");
	}

	FBox LevelBounds(UWorld* World)
	{
		if (World == nullptr)
		{
			return FBox(ForceInit);
		}
		if (const UWorldPartition* Partition = World->GetWorldPartition())
		{
			const FBox Editor = Partition->GetEditorWorldBounds();
			if (Editor.IsValid)
			{
				return Editor;
			}
		}
		FBox Bounds(ForceInit);
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			if (It->IsA<ALandscapeProxy>() || It->IsA<AStaticMeshActor>())
			{
				// bNonColliding: a mesh without collision is still the site.
				Bounds += It->GetComponentsBoundingBox(/*bNonColliding*/ true);
			}
		}
		return Bounds;
	}

	TArray<FMantlePlaceGeoPoint> ConvertCorners(UWorld* World, const FBox& Bounds, bool bNative)
	{
		TArray<FMantlePlaceGeoPoint> Points;
		if (!Bounds.IsValid)
		{
			return Points;
		}
		AActor* Georeference = FindActorOfClassPath(World, bNative ? NativeGeoreferenceClassPath() : CesiumGeoreferenceClassPath());
		if (Georeference == nullptr)
		{
			return Points;
		}

		const double Z = Bounds.GetCenter().Z;
		const FVector Corners[4] = {
			FVector(Bounds.Min.X, Bounds.Min.Y, Z),
			FVector(Bounds.Max.X, Bounds.Min.Y, Z),
			FVector(Bounds.Max.X, Bounds.Max.Y, Z),
			FVector(Bounds.Min.X, Bounds.Max.Y, Z),
		};
		for (const FVector& Corner : Corners)
		{
			FMantlePlaceGeoPoint Point;
			if (!(bNative ? ConvertNative(Georeference, Corner, Point) : ConvertCesium(Georeference, Corner, Point)))
			{
				// One corner that cannot be converted is a georeference this code cannot read: no
				// source, rather than the box of the corners that did convert.
				Points.Reset();
				return Points;
			}
			Points.Add(Point);
		}
		return Points;
	}

	TArray<FMantlePlaceExtent> ImportExtents(UWorld* World)
	{
		TArray<FMantlePlaceExtent> Extents;
		if (World == nullptr)
		{
			return Extents;
		}
		TSet<FString> Seen;
		const FString Prefix = MantlePlaceImportNaming::ImportTagPrefix();
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			FString Identity;
			if (!TagValue(**It, Prefix, Identity) || Seen.Contains(Identity))
			{
				continue;
			}
			Seen.Add(Identity);

			// The record is keyed on the SHORT identity, so two orders can share a file name; only a
			// record that carries THIS identity in full is this order's.
			MantlePlaceImportProvenance::FRecord Record;
			if (MantlePlaceImportProvenance::Read(Identity, Record) && Record.Identity == Identity && Record.bHasExtent)
			{
				Extents.Add(Record.Extent);
			}
		}
		return Extents;
	}

	TArray<FMantlePlaceExtentBoxState> ExtentBoxes(UWorld* World)
	{
		TArray<FMantlePlaceExtentBoxState> Boxes;
		if (World == nullptr)
		{
			return Boxes;
		}
		for (TActorIterator<AMantlePlaceExtentBox> It(World); It; ++It)
		{
			Boxes.Add(It->GetState());
		}
		return Boxes;
	}

	FMantlePlaceLevelState Gather(UWorld* World)
	{
		FMantlePlaceLevelState State;
		State.ImportExtents = ImportExtents(World);
		const FBox Bounds = LevelBounds(World);
		State.NativeGeoreferencedPoints = ConvertCorners(World, Bounds, /*bNative*/ true);
		State.CesiumGeoreferencedPoints = ConvertCorners(World, Bounds, /*bNative*/ false);
		State.ExtentBoxes = ExtentBoxes(World);
		return State;
	}

	FMantlePlaceResolvedExtent Resolve(UWorld* World)
	{
		return FMantlePlaceCuratorExtentLogic::Resolve(Gather(World));
	}

	AMantlePlaceExtentBox* PlaceExtentBox(UWorld* World)
	{
		if (World == nullptr)
		{
			return nullptr;
		}
		// One box is the site. A second press selects the box that is there rather than stacking another.
		for (TActorIterator<AMantlePlaceExtentBox> It(World); It; ++It)
		{
			Select(*It);
			return *It;
		}

		const FScopedTransaction Transaction(LOCTEXT("PlaceExtentBox", "Place Mantle Place Extent Box"));

		const FBox Bounds = LevelBounds(World);
		FActorSpawnParameters Parameters;
		Parameters.ObjectFlags |= RF_Transactional;
		AMantlePlaceExtentBox* Box = World->SpawnActor<AMantlePlaceExtentBox>(
			Bounds.IsValid ? Bounds.GetCenter() : FVector::ZeroVector, FRotator::ZeroRotator, Parameters);
		if (Box == nullptr)
		{
			return nullptr;
		}
		Box->SetActorLabel(MantlePlaceImportNaming::ExtentBoxLabel());
		Select(Box);
		return Box;
	}
}

#undef LOCTEXT_NAMESPACE
