// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlacePublicRecordsLogic.h"

#include "Components/SceneComponent.h"
#include "Components/SplineComponent.h"
#include "Engine/World.h"
#include "LandscapeProxy.h"
#include "Misc/FileHelper.h"

namespace
{
using namespace MantlePlaceManifestJson;

bool IsRecordFilePath(const FString& Path)
{
	return Path.EndsWith(TEXT(".gpkg"), ESearchCase::IgnoreCase) || Path.EndsWith(TEXT(".dxf"), ESearchCase::IgnoreCase);
}

FMantlePlaceRecordFile ReadFile(
	const FString& Kind, const TSharedPtr<FJsonObject>& Entry, const TSharedPtr<FJsonObject>& Files)
{
	FMantlePlaceRecordFile File;
	File.Kind = Kind;
	File.Path = GetString(Entry, TEXT("path"));
	File.Sha256 = GetString(Entry, TEXT("sha256"));
	File.Source = GetString(Entry, TEXT("source"));
	File.License = GetString(Entry, TEXT("license"));
	if (const TSharedPtr<FJsonObject>* Frame = GetObject(Entry, TEXT("file_frame")))
	{
		File.Frame.bStated = true;
		File.Frame.Type = GetString(*Frame, TEXT("type"));
		File.Frame.Crs = GetString(*Frame, TEXT("crs"));
		File.Frame.HorizontalUnit = GetString(*Frame, TEXT("horizontal_unit"));
		File.Frame.VerticalUnit = GetString(*Frame, TEXT("vertical_unit"));
	}
	if (File.Sha256.IsEmpty())
	{
		if (const TSharedPtr<FJsonObject>* Listed = GetObject(Files, *File.Path))
		{
			File.Sha256 = GetString(*Listed, TEXT("sha256"));
		}
	}
	return File;
}

void ApplyPublishedEntry(FMantlePlaceRecordFile& File, const TSharedPtr<FJsonObject>& Root)
{
	const TSharedPtr<FJsonObject> Entry = FindFileEntry(Root, File.Path);
	if (!Entry.IsValid())
	{
		return;
	}
	if (!File.Frame.bStated)
	{
		const TSharedPtr<FJsonObject>* Frame = GetObject(Entry, TEXT("frame"));
		bool bPlaced = true;
		if (Frame != nullptr)
		{
			(*Frame)->TryGetBoolField(TEXT("placed"), bPlaced);
		}
		if (Frame != nullptr && bPlaced)
		{
			FString Crs;
			if (const TSharedPtr<FJsonObject>* Horizontal = GetObject(*Frame, TEXT("horizontal_crs")))
			{
				Crs = GetString(*Horizontal, TEXT("code"));
			}
			const bool bHasFactor = (*Frame)->HasTypedField<EJson::Number>(TEXT("unit_to_metres"));
			File.Frame = FMantlePlacePublicRecordsLogic::StatedFrame(
				FMantlePlacePublicRecordsLogic::FrameTypeOfCoordinateMode(GetString(*Frame, TEXT("coordinate_mode"))), Crs,
				bHasFactor, bHasFactor ? (*Frame)->GetNumberField(TEXT("unit_to_metres")) : 0.0);
		}
	}
	if (File.License.IsEmpty())
	{
		File.License = GetString(Entry, TEXT("license"));
	}
	if (File.Source.IsEmpty())
	{
		const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
		TArray<FString> Named;
		if (Entry->TryGetArrayField(TEXT("sources"), Sources) && Sources != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& Value : *Sources)
			{
				const TSharedPtr<FJsonObject> Source = Value.IsValid() ? Value->AsObject() : TSharedPtr<FJsonObject>();
				const FString Name = Source.IsValid() ? GetString(Source, TEXT("name")) : FString();
				if (!Name.IsEmpty())
				{
					const FString Link = GetString(Source, TEXT("link"));
					Named.Add(Link.IsEmpty() ? Name : FString::Printf(TEXT("%s (%s)"), *Name, *Link));
				}
			}
		}
		File.Source = FString::Join(Named, TEXT("; "));
	}
}

FMantlePlaceRecordPointer ReadPointer(const TSharedPtr<FJsonObject>& Row)
{
	FMantlePlaceRecordPointer Pointer;
	Pointer.RecordHolder = GetString(Row, TEXT("record_holder"));
	Pointer.Jurisdiction = GetString(Row, TEXT("jurisdiction"));
	Pointer.State = GetString(Row, TEXT("state"));
	Pointer.Link = GetString(Row, TEXT("link"));
	Pointer.Reason = GetString(Row, TEXT("reason"));
	Pointer.Basis = GetString(Row, TEXT("basis"));
	Pointer.SourceId = GetString(Row, TEXT("source_id"));
	return Pointer;
}

bool SamePointer(const FMantlePlaceRecordPointer& A, const FMantlePlaceRecordPointer& B)
{
	return A.RecordHolder == B.RecordHolder && A.Jurisdiction == B.Jurisdiction && A.Reason == B.Reason
	       && A.SourceId == B.SourceId;
}

void AddPointer(FMantlePlaceRecordLayer& Layer, const FMantlePlaceRecordPointer& Pointer)
{
	for (const FMantlePlaceRecordPointer& Have : Layer.Pointers)
	{
		if (SamePointer(Have, Pointer))
		{
			return;
		}
	}
	Layer.Pointers.Add(Pointer);
}

bool ReadLayer(
	const FString& Key, const TSharedPtr<FJsonObject>& Block, const TSharedPtr<FJsonObject>& Layout,
	const TSharedPtr<FJsonObject>& Files, const TSharedPtr<FJsonObject>& Root, FMantlePlaceRecordLayer& Layer)
{
	const bool bKnown = FMantlePlacePublicRecordsLogic::IsKnownFamily(Key);

	bool bLooksLikeFamily = bKnown;
	if (!bKnown && Key != TEXT("completeness"))
	{
		for (const TCHAR* Kind : { TEXT("gpkg"), TEXT("dxf") })
		{
			const TSharedPtr<FJsonObject>* Entry = GetObject(Block, Kind);
			if (Entry != nullptr && IsRecordFilePath(GetString(*Entry, TEXT("path"))))
			{
				bLooksLikeFamily = true;
			}
		}
		const TArray<TSharedPtr<FJsonValue>>* Pointers = nullptr;
		if (Block->TryGetArrayField(TEXT("record_pointers"), Pointers) && Pointers != nullptr)
		{
			bLooksLikeFamily = true;
		}
	}
	if (!bLooksLikeFamily)
	{
		return false;
	}

	Layer = FMantlePlaceRecordLayer();
	Layer.Family = Key;
	Layer.Credit = GetString(Block, TEXT("credit"));
	Layer.Disclaimer = GetString(Block, TEXT("disclaimer"));
	Layer.Notice = GetString(Block, TEXT("notice"));

	TArray<FString> EntryKeys;
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Member : Block->Values)
	{
		EntryKeys.Add(Member.Key);
	}
	EntryKeys.Sort([](const FString& A, const FString& B)
	{
		auto Rank = [](const FString& Kind) { return Kind == TEXT("gpkg") ? 0 : (Kind == TEXT("dxf") ? 1 : 2); };
		return Rank(A) != Rank(B) ? Rank(A) < Rank(B) : A < B;
	});
	for (const FString& EntryKey : EntryKeys)
	{
		const TSharedPtr<FJsonObject>* Entry = GetObject(Block, *EntryKey);
		if (Entry == nullptr || !IsRecordFilePath(GetString(*Entry, TEXT("path"))))
		{
			continue;
		}
		FMantlePlaceRecordFile File = ReadFile(EntryKey, *Entry, Files);
		ApplyPublishedEntry(File, Root);
		if (Layer.Credit.IsEmpty())
		{
			Layer.Credit = GetString(*Entry, TEXT("credit"));
		}
		if (Layer.Disclaimer.IsEmpty())
		{
			Layer.Disclaimer = GetString(*Entry, TEXT("disclaimer"));
		}
		if (Layer.Notice.IsEmpty())
		{
			Layer.Notice = GetString(*Entry, TEXT("notice"));
		}
		Layer.Files.Add(MoveTemp(File));
	}

	auto AddFromLayout = [&](const FString& LayoutKey, const TCHAR* Kind)
	{
		const FString Path = GetString(Layout, *LayoutKey);
		if (Path.IsEmpty() || !IsRecordFilePath(Path))
		{
			return;
		}
		for (const FMantlePlaceRecordFile& Have : Layer.Files)
		{
			if (Have.Path == Path)
			{
				return;
			}
		}
		FMantlePlaceRecordFile File;
		File.Kind = Kind;
		File.Path = Path;
		if (const TSharedPtr<FJsonObject>* Listed = GetObject(Files, *Path))
		{
			File.Sha256 = GetString(*Listed, TEXT("sha256"));
		}
		ApplyPublishedEntry(File, Root);
		Layer.Files.Add(MoveTemp(File));
	};
	AddFromLayout(Key + TEXT("_gpkg"), TEXT("gpkg"));
	AddFromLayout(Key + TEXT("_dxf"), TEXT("dxf"));
	if (Key == TEXT("flood"))
	{
		AddFromLayout(TEXT("flood_zones"), TEXT("gpkg"));
	}

	const TArray<TSharedPtr<FJsonValue>>* Pointers = nullptr;
	if (Block->TryGetArrayField(TEXT("record_pointers"), Pointers) && Pointers != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Pointers)
		{
			if (Value.IsValid() && Value->AsObject().IsValid())
			{
				AddPointer(Layer, ReadPointer(Value->AsObject()));
			}
		}
	}
	return Layer.Files.Num() > 0 || Layer.Pointers.Num() > 0;
}

class FPublicRecordsHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("public_records")); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::PublicRecords; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactPublicRecords", "Public records"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		FMantlePlacePublicRecordsBlock& Block = M.PublicRecords;
		Block = FMantlePlacePublicRecordsBlock();
		if (!In.Root.IsValid())
		{
			return true;
		}
		const TSharedPtr<FJsonObject>* LayoutPtr = GetObject(In.Root, TEXT("layout"));
		const TSharedPtr<FJsonObject>* FilesPtr = GetObject(In.Root, TEXT("files"));
		const TSharedPtr<FJsonObject> Layout = LayoutPtr != nullptr ? *LayoutPtr : TSharedPtr<FJsonObject>();
		const TSharedPtr<FJsonObject> Files = FilesPtr != nullptr ? *FilesPtr : TSharedPtr<FJsonObject>();

		for (const TPair<FString, TSharedPtr<FJsonValue>>& Member : In.Root->Values)
		{
			const TSharedPtr<FJsonObject>* MemberObject = nullptr;
			if (!Member.Value.IsValid() || !Member.Value->TryGetObject(MemberObject) || MemberObject == nullptr)
			{
				continue;
			}
			FMantlePlaceRecordLayer Layer;
			if (ReadLayer(Member.Key, *MemberObject, Layout, Files, In.Root, Layer))
			{
				Block.Layers.Add(MoveTemp(Layer));
			}
		}

		if (const TSharedPtr<FJsonObject>* AttributionPtr = GetObject(In.Root, TEXT("attribution")))
		{
			Block.BundleNotice = GetString(*AttributionPtr, TEXT("notice"));
		}

		if (const TSharedPtr<FJsonObject>* CompletenessPtr = GetObject(In.Root, TEXT("completeness")))
		{
			const TArray<TSharedPtr<FJsonValue>>* Pointers = nullptr;
			if ((*CompletenessPtr)->TryGetArrayField(TEXT("record_pointers"), Pointers) && Pointers != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& Value : *Pointers)
				{
					const TSharedPtr<FJsonObject> Row = Value.IsValid() ? Value->AsObject() : TSharedPtr<FJsonObject>();
					if (!Row.IsValid())
					{
						continue;
					}
					const FString Family = GetString(Row, TEXT("layer"));
					FMantlePlaceRecordLayer* Target = Block.Layers.FindByPredicate(
						[&Family](const FMantlePlaceRecordLayer& Have) { return Have.Family == Family; });
					if (Target == nullptr)
					{
						FMantlePlaceRecordLayer Created;
						Created.Family = Family;
						Block.Layers.Add(MoveTemp(Created));
						Target = &Block.Layers.Last();
					}
					AddPointer(*Target, ReadPointer(Row));
				}
			}
			const TArray<TSharedPtr<FJsonValue>>* Skipped = nullptr;
			if ((*CompletenessPtr)->TryGetArrayField(TEXT("skipped_sources"), Skipped) && Skipped != nullptr)
			{
				for (const TSharedPtr<FJsonValue>& Value : *Skipped)
				{
					const TSharedPtr<FJsonObject> Row = Value.IsValid() ? Value->AsObject() : TSharedPtr<FJsonObject>();
					if (!Row.IsValid())
					{
						continue;
					}
					FMantlePlaceSkippedSource Source;
					Source.Layer = GetString(Row, TEXT("layer"));
					Source.SourceId = GetString(Row, TEXT("source_id"));
					Source.RecordHolder = GetString(Row, TEXT("record_holder"));
					Source.Reason = GetString(Row, TEXT("reason"));
					Block.SkippedSources.Add(MoveTemp(Source));
				}
			}
		}

		FMantlePlacePublicRecordsLogic::OrderLayers(Block.Layers);
		Block.bPresent = Block.Layers.Num() > 0 || Block.SkippedSources.Num() > 0 || Block.NotHeld.Num() > 0;
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.PublicRecords.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		for (const FMantlePlaceRecordLayer& Layer : M.PublicRecords.Layers)
		{
			for (const FMantlePlaceRecordFile& File : Layer.Files)
			{
				if (!File.Path.IsEmpty())
				{
					Out.Add(FMantlePlaceDeclaredArtifact{ File.Path, File.Sha256 });
				}
			}
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlacePublicRecordsBlock& Block = Ctx.Manifest.PublicRecords;
		if (!Block.bPresent)
		{
			return;
		}
		Ctx.Log.Add(FMantlePlacePublicRecordsLogic::StandingNotice());
		if (!Block.BundleNotice.IsEmpty())
		{
			Ctx.Log.Add(FString::Printf(TEXT("The bundle's notice: %s"), *Block.BundleNotice));
		}
		for (const FMantlePlaceRecordLayer& Layer : Block.Layers)
		{
			if (Layer.Files.Num() == 0)
			{
				continue;
			}
			Ctx.Log.Append(FMantlePlacePublicRecordsLogic::DescribeLayer(Layer));
			ImportLayer(Ctx, Layer);
		}
		Ctx.Log.Append(FMantlePlacePublicRecordsLogic::DescribeNotDelivered(Block));
	}

private:
	static void ImportLayer(FMantlePlaceImportContext& Ctx, const FMantlePlaceRecordLayer& Layer)
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		const FString Name = FMantlePlacePublicRecordsLogic::DisplayName(Layer.Family);

		TArray<FMantlePlaceRecordPolyline> Lines;
		TArray<FString> Drawn;
		int32 LinesDrawn = 0;
		int32 PointsDrawn = 0;
		bool bAnyDxf = false;
		for (const FMantlePlaceRecordFile& File : Layer.Files)
		{
			if (!File.IsDxf())
			{
				continue;
			}
			bAnyDxf = true;
			FString Refusal;
			if (!FMantlePlacePublicRecordsLogic::CanPlaceFrame(File.Frame, Manifest.Georef.CrsProjected, Refusal))
			{
				Ctx.Log.Add(FString::Printf(TEXT("%s not placed (%s): %s."), *Name, *File.Path, *Refusal));
				continue;
			}
			TArray<uint8> Bytes;
			if (!Ctx.ReadBytes(File.Path, Bytes))
			{
				Ctx.Log.Add(FString::Printf(TEXT("%s skipped: bundle is missing %s."), *Name, *File.Path));
				continue;
			}
			FString Text, Error;
			FFileHelper::BufferToString(Text, Bytes.GetData(), Bytes.Num());
			TArray<FMantlePlaceRecordPolyline> FileLines;
			TArray<FVector2D> FilePoints;
			int32 EntitiesSkipped = 0;
			if (!FMantlePlacePublicRecordsLogic::ParseDxf(Text, FileLines, FilePoints, EntitiesSkipped, Error))
			{
				Ctx.Log.Add(FString::Printf(TEXT("%s skipped (%s): %s."), *Name, *File.Path, *Error));
				continue;
			}
			const int32 FileLineCount = FileLines.Num();
			for (const FVector2D& Point : FilePoints)
			{
				FileLines.Add(FMantlePlacePublicRecordsLogic::MarkerOutline(Point));
			}
			if (!FMantlePlacePublicRecordsLogic::AnyVertexOnSite(
					FileLines, Manifest.Georef.OriginEastingM, Manifest.Georef.OriginNorthingM, Manifest.GetAoiSizeUeCm()))
			{
				Ctx.Log.Add(FString::Printf(
					TEXT("%s not placed (%s): none of its linework falls on the landscape the bundle publishes, so the "
					     "file is not in this frame."),
					*Name, *File.Path));
				continue;
			}
			if (EntitiesSkipped > 0)
			{
				Ctx.Log.Add(FString::Printf(
					TEXT("%s: %d drawing entit%s in %s that are not lines, areas or points were left out."), *Name, EntitiesSkipped,
					EntitiesSkipped == 1 ? TEXT("y") : TEXT("ies"), *File.Path));
			}
			LinesDrawn += FileLineCount;
			PointsDrawn += FilePoints.Num();
			Lines.Append(FileLines);
			Drawn.Add(File.Path);
		}

		if (!bAnyDxf)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("%s: the bundle carries the GeoPackage only, so there is no linework to show. The file stays in the "
				     "bundle, verified, for a GIS."),
				*Name));
			return;
		}
		if (Drawn.Num() == 0)
		{
			return;
		}
		if (Lines.Num() == 0)
		{
			Ctx.Log.Add(FString::Printf(TEXT("%s: the drawing holds no lines, areas or points to show."), *Name));
			return;
		}

		AActor* Actor = Ctx.World->SpawnActor<AActor>();
		if (Actor == nullptr)
		{
			Ctx.Log.Add(FString::Printf(TEXT("%s: the overlay actor could not be spawned."), *Name));
			return;
		}
		USceneComponent* Root = NewObject<USceneComponent>(Actor, USceneComponent::StaticClass(), TEXT("RecordRoot"), RF_Transactional);
		Actor->SetRootComponent(Root);
		Actor->AddInstanceComponent(Root);
		Root->RegisterComponent();

		const FLinearColor Color = FLinearColor::MakeFromHSV8(static_cast<uint8>(GetTypeHash(Layer.Family) % 256u), 200, 255);
		const ALandscapeProxy* Landscape = nullptr;
		for (AActor* Target : Ctx.DrapeTargets)
		{
			Landscape = Cast<ALandscapeProxy>(Target);
			if (Landscape != nullptr)
			{
				break;
			}
		}
		const double FlatZ = Manifest.GetMeshLocation().Z;
		int32 PointsOffGround = 0;

		for (const FMantlePlaceRecordPolyline& Line : Lines)
		{
			USplineComponent* Spline = NewObject<USplineComponent>(Actor, USplineComponent::StaticClass(), NAME_None, RF_Transactional);
			Spline->SetupAttachment(Root);
			Actor->AddInstanceComponent(Spline);
			Spline->RegisterComponent();
			Spline->ClearSplinePoints(false);
			for (const FVector2D& Point : Line.Points)
			{
				FVector Position = FMantlePlaceVaultManifest::ProjectedToUeCm(
					Point.X - Manifest.Georef.OriginEastingM, Point.Y - Manifest.Georef.OriginNorthingM, 0.0);
				Position.Z = FlatZ;
				Position = Manifest.ToLevelFrame(Position);
				if (Landscape != nullptr)
				{
					const TOptional<float> Height = Landscape->GetHeightAtLocation(Position);
					if (Height.IsSet())
					{
						Position.Z = Height.GetValue();
					}
					else
					{
						++PointsOffGround;
					}
				}
				Spline->AddSplinePoint(Position, ESplineCoordinateSpace::World, false);
			}
			for (int32 Index = 0; Index < Spline->GetNumberOfSplinePoints(); ++Index)
			{
				Spline->SetSplinePointType(Index, ESplinePointType::Linear, false);
			}
			Spline->SetClosedLoop(Line.bClosed, false);
			Spline->SetUnselectedSplineSegmentColor(Color);
			Spline->UpdateSpline();
		}

		Ctx.SourcePaths = Drawn;
		Ctx.Claim(Actor, MantlePlaceImportNaming::PublicRecordLabel(Ctx.Identity, Layer.Family));
		Actor->Tags.Add(FName(*FString::Printf(TEXT("record_family=%s"), *Layer.Family)));
		Ctx.Result.CreatedActors.Add(Actor->GetActorLabel());
		Ctx.Log.Add(FString::Printf(
			TEXT("%s: %d line(s) or area outline(s) and %d point marker(s) from %s, one overlay (%s). Hide or show it in the "
			     "Outliner to toggle the layer."),
			*Name, LinesDrawn, PointsDrawn, *FString::Join(Drawn, TEXT(", ")), *Actor->GetActorLabel()));
		if (Landscape == nullptr)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("%s: no Landscape in this import to follow, so the overlay lies flat at the bundle's ground reference height."),
				*Name));
		}
		else if (PointsOffGround > 0)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("%s: %d point(s) fall off the Landscape and lie at the ground reference height."), *Name, PointsOffGround));
		}
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakePublicRecords() { return MakeUnique<FPublicRecordsHandler>(); }
}
