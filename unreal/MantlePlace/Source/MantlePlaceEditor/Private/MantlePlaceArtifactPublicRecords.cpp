// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// The public-record layers of the bundle's top level (parcels, zoning, flood zones, and the families
// that follow them in the `Records/` folder), as one overlay per family: a single actor holding one
// spline per DXF line, which the user toggles by hiding the actor in the Outliner. Like the buildings
// and the road splines this is content, not a terrain representation: it imports whenever the bundle
// ships a layer, whatever the Mode, and a failure is non-fatal.
//
// The layers are the public record passed through unchanged. This handler reads the manifest, draws
// the linework and tells the user what the manifest says about each layer (credit, disclaimer,
// notice) and about each record it does not deliver; it derives nothing and edits nothing. A file is
// found through the path its own entry states (or its `layout` key), never by folder, so a bundle
// built before the `Records/` folder (`Parcels/`, `Zoning/`, `Flood/` at its root) is read by the
// same code, and a family this reader has never met is read generically.
//
// What is drawn is DXF linework, and only where the file states its frame and the frame is this
// host's (HPS-53). The GeoPackage of a family is declared for the integrity chain and kept in the
// bundle; reading SQLite is not something this plugin takes a dependency on. The decisions are
// FMantlePlacePublicRecordsLogic's; the parse of the manifest is here because it walks arbitrary
// members, and the placement is here because it spawns actors.

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
		// The file's own statement of its frame, verbatim and judged nowhere here (HPS-53): whether it
		// is this host's frame is FMantlePlacePublicRecordsLogic::CanPlaceFrame's call.
		File.Frame.bStated = true;
		File.Frame.Type = GetString(*Frame, TEXT("type"));
		File.Frame.Crs = GetString(*Frame, TEXT("crs"));
		File.Frame.HorizontalUnit = GetString(*Frame, TEXT("horizontal_unit"));
		File.Frame.VerticalUnit = GetString(*Frame, TEXT("vertical_unit"));
	}
	if (File.Sha256.IsEmpty())
	{
		// The same file is listed in `files` with its digest; an entry that states none borrows it.
		if (const TSharedPtr<FJsonObject>* Listed = GetObject(Files, *File.Path))
		{
			File.Sha256 = GetString(*Listed, TEXT("sha256"));
		}
	}
	return File;
}

FMantlePlaceRecordPointer ReadPointer(const TSharedPtr<FJsonObject>& Row)
{
	FMantlePlaceRecordPointer Pointer;
	Pointer.RecordHolder = GetString(Row, TEXT("record_holder"));
	Pointer.Jurisdiction = GetString(Row, TEXT("jurisdiction"));
	Pointer.State = GetString(Row, TEXT("state"));
	Pointer.Link = GetString(Row, TEXT("link")); // null reads as empty
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

/** Whether a top-level member is a public-record family block, and if so its layer (otherwise Layer stays empty). */
bool ReadLayer(
	const FString& Key, const TSharedPtr<FJsonObject>& Block, const TSharedPtr<FJsonObject>& Layout,
	const TSharedPtr<FJsonObject>& Files, FMantlePlaceRecordLayer& Layer)
{
	const bool bKnown = FMantlePlacePublicRecordsLogic::IsKnownFamily(Key);

	// A family this reader has not met is recognised by its shape: file entries under `gpkg` / `dxf`,
	// or record pointers. `completeness` carries the pointers of every layer and is not one.
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

	// Each file entry is located by the `path` it states. Sorted by key, so the order does not depend
	// on how the manifest happened to be written.
	TArray<FString> EntryKeys;
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Member : Block->Values)
	{
		EntryKeys.Add(Member.Key);
	}
	EntryKeys.Sort([](const FString& A, const FString& B)
	{
		// gpkg and dxf first, in that order, then any other key by name.
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

	// A file the block does not point at but the `layout` does: the same pointer doctrine, one level
	// up. `flood_zones` is the flood family's GeoPackage.
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
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactPublicRecords", "Public records"); }

	// Read from the manifest's top level, before the `unreal`-presence check, as the road splines are:
	// the records are addressed to every host, and a bundle with nothing else Unreal imports still
	// tells the user what it carries.
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
			if (ReadLayer(Member.Key, *MemberObject, Layout, Files, Layer))
			{
				Block.Layers.Add(MoveTemp(Layer));
			}
		}

		if (const TSharedPtr<FJsonObject>* CompletenessPtr = GetObject(In.Root, TEXT("completeness")))
		{
			// The pointers of every layer also stand here, each naming its layer: a layer with no block
			// of its own (a holder that delivered nothing at all) is still shown to the user.
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

		// A layer with an empty family key could only come from a pointer row naming no layer: it is
		// kept (the holder is worth showing) and reads as "Record" in the sentence.
		FMantlePlacePublicRecordsLogic::OrderLayers(Block.Layers);
		Block.bPresent = Block.Layers.Num() > 0 || Block.SkippedSources.Num() > 0;
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.PublicRecords.bPresent; }

	// Every file a layer points at is in the integrity chain, the GeoPackages the editor does not read
	// included: a record is verified when the bundle carries it, whether or not it is drawn. A file
	// with no digest is skipped and reported unverified, never refused.
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
		// The notice comes first and whatever else follows: it is the one thing every public-record
		// import says, whether or not the manifest carries a notice of its own for a layer.
		Ctx.Log.Add(FMantlePlacePublicRecordsLogic::StandingNotice());
		for (const FMantlePlaceRecordLayer& Layer : Block.Layers)
		{
			if (Layer.Files.Num() == 0)
			{
				continue; // a holder that delivered nothing: its pointer is reported below
			}
			Ctx.Log.Append(FMantlePlacePublicRecordsLogic::DescribeLayer(Layer));
			ImportLayer(Ctx, Layer);
		}
		// A record the bundle does not deliver is shown with where its holder publishes it, so the
		// layer is never silently missing.
		Ctx.Log.Append(FMantlePlacePublicRecordsLogic::DescribeNotDelivered(Block));
	}

private:
	static void ImportLayer(FMantlePlaceImportContext& Ctx, const FMantlePlaceRecordLayer& Layer)
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		const FString Name = FMantlePlacePublicRecordsLogic::DisplayName(Layer.Family);

		TArray<FMantlePlaceRecordPolyline> Lines;
		TArray<FString> Drawn;
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
			int32 EntitiesSkipped = 0;
			if (!FMantlePlacePublicRecordsLogic::ParseDxf(Text, FileLines, EntitiesSkipped, Error))
			{
				Ctx.Log.Add(FString::Printf(TEXT("%s skipped (%s): %s."), *Name, *File.Path, *Error));
				continue;
			}
			// The one-way backstop behind a stated frame: a producer can state a frame wrongly.
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
					TEXT("%s: %d drawing entit%s in %s that are not lines were left out."), *Name, EntitiesSkipped,
					EntitiesSkipped == 1 ? TEXT("y") : TEXT("ies"), *File.Path));
			}
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
			return; // each refusal above is already named
		}
		if (Lines.Num() == 0)
		{
			Ctx.Log.Add(FString::Printf(TEXT("%s: the linework holds no lines to show."), *Name));
			return;
		}

		// One actor per family, so the layer is one thing to hide or show. Its lines are splines under
		// it, laid on the imported ground; no height from the record is used.
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
		// With no Landscape to follow (a mesh-only import) the overlay lies flat at the ground reference
		// height the manifest publishes, and the log says so.
		const double FlatZ = Manifest.GetMeshLocation().Z;
		int32 PointsOffGround = 0;

		for (const FMantlePlaceRecordPolyline& Line : Lines)
		{
			USplineComponent* Spline = NewObject<USplineComponent>(Actor, USplineComponent::StaticClass(), NAME_None, RF_Transactional);
			Spline->SetupAttachment(Root);
			Actor->AddInstanceComponent(Spline);
			Spline->RegisterComponent();
			Spline->ClearSplinePoints(/*bUpdateSpline*/ false);
			for (const FVector2D& Point : Line.Points)
			{
				FVector Position = FMantlePlaceVaultManifest::ProjectedToUeCm(
					Point.X - Manifest.Georef.OriginEastingM, Point.Y - Manifest.Georef.OriginNorthingM, 0.0);
				Position.Z = FlatZ;
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
				Spline->AddSplinePoint(Position, ESplineCoordinateSpace::World, /*bUpdateSpline*/ false);
			}
			// Straight segments between the file's vertices: a parcel edge is not a curve.
			for (int32 Index = 0; Index < Spline->GetNumberOfSplinePoints(); ++Index)
			{
				Spline->SetSplinePointType(Index, ESplinePointType::Linear, /*bUpdateSpline*/ false);
			}
			Spline->SetClosedLoop(Line.bClosed, /*bUpdateSpline*/ false);
			Spline->SetUnselectedSplineSegmentColor(Color);
			Spline->UpdateSpline();
		}

		Ctx.Claim(Actor, MantlePlaceImportNaming::PublicRecordLabel(Ctx.Identity, Layer.Family));
		Actor->Tags.Add(FName(*FString::Printf(TEXT("record_family=%s"), *Layer.Family)));
		Ctx.Result.CreatedActors.Add(Actor->GetActorLabel());
		Ctx.Log.Add(FString::Printf(
			TEXT("%s: %d line(s) from %s, one overlay (%s). Hide or show it in the Outliner to toggle the layer."), *Name,
			Lines.Num(), *FString::Join(Drawn, TEXT(", ")), *Actor->GetActorLabel()));
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
