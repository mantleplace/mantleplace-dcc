// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceManifestThreeLogic.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLandscapeWeightsLogic.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceOdblLogic.h"
#include "MantlePlacePublicRecordsLogic.h"

#include "Dom/JsonObject.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
using namespace MantlePlaceManifestJson;

const FName DrapeId(TEXT("drape"));
const FName CoverageId(TEXT("coverage_rasters"));
const FName BuildingsId(TEXT("buildings"));
const FName RoadsId(TEXT("road_splines"));
const FName TreesId(TEXT("foliage_points"));
const FName RecordsId(TEXT("public_records"));

bool IsMetre(const TSharedPtr<FJsonObject>* Unit)
{
	return Unit != nullptr && (*Unit)->HasTypedField<EJson::Number>(TEXT("to_metres"))
		&& (*Unit)->GetNumberField(TEXT("to_metres")) == 1.0;
}

FString UnitWords(const TSharedPtr<FJsonObject>* Unit)
{
	if (Unit == nullptr)
	{
		return TEXT("no unit");
	}
	return FString::Printf(TEXT("%s (%g m)"), *GetString(*Unit, TEXT("name")), GetNumber(*Unit, TEXT("to_metres")));
}

bool IsMetreRasterUnit(const FString& Unit)
{
	return Unit.IsEmpty() || Unit == TEXT("m") || Unit == TEXT("metre");
}

bool ReadNumbers(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, int32 Expected, TArray<double>& Out)
{
	Out.Reset();
	const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
	if (!Obj.IsValid() || !Obj->TryGetArrayField(Field, Values) || Values->Num() != Expected)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Values)
	{
		double Number = 0.0;
		if (!Value.IsValid() || !Value->TryGetNumber(Number))
		{
			return false;
		}
		Out.Add(Number);
	}
	return true;
}

struct FFound
{
	const FMantlePlaceBundleFile* File = nullptr;
	TSharedPtr<FJsonObject> Entry;
};

struct FThreeContext
{
	const FMantlePlaceBundleRead& Read;
	TSharedPtr<FJsonObject> Catalog;
	FMantlePlaceVaultManifest& M;

	FFound Find(const TCHAR* Path, FName Step) const
	{
		FFound Found;
		Found.File = Read.Files.FindByPredicate(
			[Path](const FMantlePlaceBundleFile& F) { return F.Path.Equals(Path, ESearchCase::IgnoreCase); });
		if (Found.File != nullptr)
		{
			Found.Entry = FindEntry(Found.File->Path);
		}
		if (Found.File == nullptr || !Found.Entry.IsValid())
		{
			const FMantlePlaceBundleSkipped* Skipped = Read.Skipped.FindByPredicate(
				[Path](const FMantlePlaceBundleSkipped& S) { return S.Path.Equals(Path, ESearchCase::IgnoreCase); });
			Skip(Step, FMantlePlaceManifestThreeLogic::NotHeldReason(Path, Skipped != nullptr ? Skipped->Reason : FString()));
			Found.File = nullptr;
		}
		return Found;
	}

	TSharedPtr<FJsonObject> FindEntry(const FString& Path) const
	{
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Catalog->Values)
		{
			if (Pair.Key.Equals(Path, ESearchCase::IgnoreCase) && Pair.Value.IsValid())
			{
				return Pair.Value->AsObject();
			}
		}
		return nullptr;
	}

	void Skip(FName Step, const FString& Reason) const
	{
		if (!M.FormatSkips.Contains(Step))
		{
			M.FormatSkips.Add(Step, Reason);
		}
	}
};

TSharedPtr<FJsonObject> LocalRaster(const FThreeContext& C, const FFound& Found, FName Step)
{
	const TSharedPtr<FJsonObject>* Raster = GetObject(Found.Entry, TEXT("raster"));
	if (Raster == nullptr || GetString(*Raster, TEXT("frame")) != TEXT("local"))
	{
		C.Skip(Step, FString::Printf(TEXT("%s does not state that it is in the local site frame, so it cannot be "
			"placed (HPS-53)."), *Found.File->Path));
		return nullptr;
	}
	return *Raster;
}

bool ReadMesh(const FThreeContext& C, const FFound& Found, FName Step, FString& OutPath, FString& OutUpAxis, FString& OutSha)
{
	const TSharedPtr<FJsonObject>* Mesh = GetObject(Found.Entry, TEXT("mesh"));
	OutUpAxis = Mesh != nullptr ? GetString(*Mesh, TEXT("up_axis")) : FString();
	if (OutUpAxis != TEXT("y"))
	{
		C.Skip(Step, FString::Printf(TEXT("%s states up axis '%s'; a glTF mesh is Y-up, and this plugin places no "
			"other (format.md section 4)."), *Found.File->Path, *OutUpAxis));
		return false;
	}
	OutPath = Found.File->EntryName;
	OutSha = GetString(Found.Entry, TEXT("sha256"));
	return true;
}

void ReadHeightmap(const FThreeContext& C, double SiteHeightM)
{
	const FName Step = MantlePlaceArtifactId::Landscape();
	const FFound Found = C.Find(FMantlePlaceManifestThreeLogic::HeightmapPath(), Step);
	if (Found.File == nullptr)
	{
		return;
	}
	const TSharedPtr<FJsonObject> Raster = LocalRaster(C, Found, Step);
	if (!Raster.IsValid())
	{
		return;
	}
	const FString& Path = Found.File->Path;
	TArray<double> Rect, Cell;
	const int32 Width = GetInt(Raster, TEXT("width"));
	const int32 Height = GetInt(Raster, TEXT("height"));
	const FString Unit = GetString(Raster, TEXT("unit"));
	if (GetString(Raster, TEXT("data_type")) != TEXT("UInt16") || !Raster->HasTypedField<EJson::Number>(TEXT("scale"))
		|| !Raster->HasTypedField<EJson::Number>(TEXT("offset")) || !IsMetreRasterUnit(Unit))
	{
		C.Skip(Step, FString::Printf(TEXT("%s does not state a UInt16 height in metres as offset + code x scale (unit: %s), "
			"so its pixels cannot be read as heights (format.md section 7)."), *Path, Unit.IsEmpty() ? TEXT("none") : *Unit));
		return;
	}
	if (!ReadNumbers(Raster, TEXT("rectangle"), 4, Rect) || !ReadNumbers(Raster, TEXT("cell_size"), 2, Cell)
		|| Cell[0] <= 0.0 || Cell[1] <= 0.0 || Rect[2] <= Rect[0] || Rect[3] <= Rect[1])
	{
		C.Skip(Step, FString::Printf(TEXT("%s states no rectangle or cell size, so it cannot be placed (HPS-53)."), *Path));
		return;
	}
	FMantlePlaceLandscapeLayout Layout;
	if (!FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(Width, Height, Layout))
	{
		C.Skip(Step, FString::Printf(TEXT("%s is %d x %d pixels, a size no Landscape layout takes."), *Path, Width, Height));
		return;
	}

	FMantlePlaceHeightmapBlock& H = C.M.Heightmap;
	const double Scale = Raster->GetNumberField(TEXT("scale"));
	const double Offset = Raster->GetNumberField(TEXT("offset"));
	H.Path = Found.File->EntryName;
	H.Sha256 = GetString(Found.Entry, TEXT("sha256"));
	H.Resolution = Width;
	H.bRow0IsNorth = true;
	H.SectionSizeQuads = Layout.SectionSizeQuads;
	H.SectionsPerComponent = Layout.SectionsPerComponent;
	H.ComponentCountX = Layout.ComponentCountX;
	H.ComponentCountY = Layout.ComponentCountY;
	H.ScaleXPercent = Cell[0] * 100.0;
	H.ScaleYPercent = Cell[1] * 100.0;
	H.ScaleZPercent = Scale * 100.0 * 128.0;
	H.LocationZOffsetCm = (SiteHeightM + Offset + 32768.0 * Scale) * 100.0;
	H.ZMinM = SiteHeightM + Offset;
	H.ZMaxM = SiteHeightM + Offset + 65535.0 * Scale;
	H.GridCentreEastM = (Rect[0] + Rect[2]) / 2.0;
	H.GridCentreNorthM = (Rect[1] + Rect[3]) / 2.0;
	H.bPresent = true;
}

void ReadMaterials(const FThreeContext& C)
{
	FMantlePlaceLandscapeLayer Layer;
	Layer.Name = TEXT("material_weights");
	const FString Folder = FMantlePlaceManifestThreeLogic::MaterialsFolder();
	for (const FMantlePlaceBundleFile& File : C.Read.Files)
	{
		if (!File.Path.StartsWith(Folder, ESearchCase::IgnoreCase) || !File.Path.EndsWith(TEXT(".png"), ESearchCase::IgnoreCase))
		{
			continue;
		}
		const TSharedPtr<FJsonObject> Entry = C.FindEntry(File.Path);
		const TSharedPtr<FJsonObject>* Raster = Entry.IsValid() ? GetObject(Entry, TEXT("raster")) : nullptr;
		if (Raster == nullptr || GetString(*Raster, TEXT("frame")) != TEXT("local")
			|| GetInt(*Raster, TEXT("width")) <= 0 || GetInt(*Raster, TEXT("height")) <= 0)
		{
			C.M.FormatNotes.Add(FString::Printf(TEXT("Paint layer left out: %s does not state its size in the local "
				"site frame (HPS-53)."), *File.Path));
			continue;
		}
		const FString Material = FPaths::GetBaseFilename(File.Path);
		FMantlePlaceUeReadyRaster Map;
		Map.Path = File.EntryName;
		Map.Sha256 = GetString(Entry, TEXT("sha256"));
		Map.Encoding = FMantlePlaceLandscapeWeightsLogic::GrayscaleEncoding;
		Map.Width = GetInt(*Raster, TEXT("width"));
		Map.Height = GetInt(*Raster, TEXT("height"));
		Map.ValueMapping.Bands = { Material };
		Layer.Materials.Add(Material);
		Layer.UeReady.Add(MoveTemp(Map));
	}
	if (Layer.Materials.Num() > 0)
	{
		C.M.LandscapeLayers.Layers.Add(MoveTemp(Layer));
		C.M.LandscapeLayers.bPresent = true;
	}
}

void ReadRoads(const FThreeContext& C, double SiteHeightM)
{
	const FFound Found = C.Find(FMantlePlaceManifestThreeLogic::RoadsPath(), RoadsId);
	if (Found.File == nullptr)
	{
		return;
	}
	FMantlePlaceRoadSplinesBlock& R = C.M.RoadSplines;
	R.SiteCsvPath = Found.File->EntryName;
	R.SiteCsvSha256 = GetString(Found.Entry, TEXT("sha256"));
	R.SiteHeightM = SiteHeightM;
	R.bSiteCsvPresent = true;
}

void ReadDrape(const FThreeContext& C)
{
	const FFound Found = C.Find(FMantlePlaceManifestThreeLogic::DrapePath(), DrapeId);
	if (Found.File == nullptr)
	{
		return;
	}
	const TSharedPtr<FJsonObject> Raster = LocalRaster(C, Found, DrapeId);
	TArray<double> Rect;
	if (!Raster.IsValid())
	{
		return;
	}
	if (!ReadNumbers(Raster, TEXT("rectangle"), 4, Rect) || Rect[2] <= Rect[0] || Rect[3] <= Rect[1])
	{
		C.Skip(DrapeId, FString::Printf(TEXT("%s states no rectangle, so it cannot be placed (HPS-53)."), *Found.File->Path));
		return;
	}
	FMantlePlaceDrapeBlock& D = C.M.Drape;
	const FMantlePlaceGeoreferenceBlock& G = C.M.Georef;
	D.Path = Found.File->EntryName;
	D.Sha256 = GetString(Found.Entry, TEXT("sha256"));
	D.LeftM = G.OriginEastingM + Rect[0];
	D.BottomM = G.OriginNorthingM + Rect[1];
	D.RightM = G.OriginEastingM + Rect[2];
	D.TopM = G.OriginNorthingM + Rect[3];
	D.bPresent = true;
}

void ReadMeshes(const FThreeContext& C)
{
	const FName TerrainId = MantlePlaceArtifactId::TerrainMesh();
	const FFound Terrain = C.Find(FMantlePlaceManifestThreeLogic::TerrainMeshPath(), TerrainId);
	if (Terrain.File != nullptr)
	{
		FMantlePlaceMeshBlock& Mesh = C.M.Mesh;
		Mesh.bPresent = ReadMesh(C, Terrain, TerrainId, Mesh.Path, Mesh.UpAxis, Mesh.Sha256);
	}
	const FFound Buildings = C.Find(FMantlePlaceManifestThreeLogic::BuildingsPath(), BuildingsId);
	if (Buildings.File != nullptr)
	{
		FMantlePlaceBuildingsBlock& B = C.M.Buildings;
		B.bPresent = ReadMesh(C, Buildings, BuildingsId, B.Path, B.UpAxis, B.Sha256);
	}
}

void ReadTrees(const FThreeContext& C, double SiteHeightM)
{
	const FFound Found = C.Find(FMantlePlaceManifestThreeLogic::TreesPath(), TreesId);
	if (Found.File == nullptr)
	{
		return;
	}
	FMantlePlaceFoliagePointsBlock& F = C.M.Foliage;
	F.Path = Found.File->EntryName;
	F.Sha256 = GetString(Found.Entry, TEXT("sha256"));
	F.Count = Found.File->bHasCount ? static_cast<int32>(Found.File->Count) : 0;
	F.Crs = C.M.Georef.CrsProjected;
	F.Units = TEXT("m");
	F.HorizontalUnits = TEXT("m");
	if (const TSharedPtr<FJsonObject>* Table = GetObject(Found.Entry, TEXT("table")))
	{
		if (const TSharedPtr<FJsonObject>* Types = GetObject(*Table, TEXT("foliage_types")))
		{
			F.FoliageTypeVocabulary = GetString(*Types, TEXT("vocabulary"));
		}
	}
	F.bSiteFrame = true;
	F.SiteHeightM = SiteHeightM;
	F.bPresent = true;
}

void ReadRecords(const FThreeContext& C, const TSharedPtr<FJsonObject>& Root)
{
	const FString Folder = FMantlePlaceManifestThreeLogic::RecordsFolder();
	FMantlePlacePublicRecordsBlock& Block = C.M.PublicRecords;

	FMantlePlaceRecordFrame Frame;
	const TSharedPtr<FJsonObject>* Frames = GetObject(Root, TEXT("frames"));
	const TSharedPtr<FJsonObject>* Georeferenced = Frames != nullptr ? GetObject(*Frames, TEXT("georeferenced")) : nullptr;
	if (Georeferenced != nullptr)
	{
		const TSharedPtr<FJsonObject>* Horizontal = GetObject(*Georeferenced, TEXT("horizontal_crs"));
		const TSharedPtr<FJsonObject>* Unit = GetObject(*Georeferenced, TEXT("horizontal_unit"));
		Frame = FMantlePlacePublicRecordsLogic::StatedFrame(TEXT("projected"),
			Horizontal != nullptr ? GetString(*Horizontal, TEXT("code")) : FString(), Unit != nullptr && (*Unit)->HasTypedField<EJson::Number>(TEXT("to_metres")),
			Unit != nullptr ? GetNumber(*Unit, TEXT("to_metres")) : 0.0, Unit != nullptr ? GetString(*Unit, TEXT("name")) : FString());
	}

	auto IsRecordFile = [&Folder](const FString& Path, FString& OutKind)
	{
		OutKind = Path.EndsWith(TEXT(".gpkg"), ESearchCase::IgnoreCase) ? TEXT("gpkg")
			: (Path.EndsWith(TEXT(".dxf"), ESearchCase::IgnoreCase) ? TEXT("dxf") : TEXT(""));
		return Path.StartsWith(Folder, ESearchCase::IgnoreCase) && !OutKind.IsEmpty();
	};
	auto FamilyOf = [](const FString& Path) { return FPaths::GetBaseFilename(Path).ToLower(); };

	for (const FMantlePlaceBundleFile& File : C.Read.Files)
	{
		FString Kind;
		const TSharedPtr<FJsonObject> Entry = C.FindEntry(File.Path);
		if (!IsRecordFile(File.Path, Kind) || !Entry.IsValid())
		{
			continue;
		}
		const FString Family = FamilyOf(File.Path);
		FMantlePlaceRecordLayer* Layer = Block.Layers.FindByPredicate(
			[&Family](const FMantlePlaceRecordLayer& Have) { return Have.Family == Family; });
		if (Layer == nullptr)
		{
			Layer = &Block.Layers.AddDefaulted_GetRef();
			Layer->Family = Family;
		}
		FMantlePlaceRecordFile Record;
		Record.Kind = Kind;
		Record.Path = File.EntryName;
		Record.Sha256 = GetString(Entry, TEXT("sha256"));
		Record.License = GetString(Entry, TEXT("license"));
		Record.Frame = Frame;
		TArray<FString> Named;
		const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
		if (Entry->TryGetArrayField(TEXT("sources"), Sources) && Sources != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& Value : *Sources)
			{
				const TSharedPtr<FJsonObject> Source = Value.IsValid() ? Value->AsObject() : nullptr;
				const FString Name = Source.IsValid() ? GetString(Source, TEXT("name")) : FString();
				if (!Name.IsEmpty())
				{
					const FString Link = GetString(Source, TEXT("link"));
					Named.Add(Link.IsEmpty() ? Name : FString::Printf(TEXT("%s (%s)"), *Name, *Link));
				}
			}
		}
		Record.Source = FString::Join(Named, TEXT("; "));
		Layer->Files.Add(MoveTemp(Record));
	}
	for (FMantlePlaceRecordLayer& Layer : Block.Layers)
	{
		Layer.Files.StableSort([](const FMantlePlaceRecordFile& A, const FMantlePlaceRecordFile& B)
		{
			return A.IsDxf() < B.IsDxf();
		});
	}

	for (const FMantlePlaceBundleSkipped& Skipped : C.Read.Skipped)
	{
		FString Kind;
		if (IsRecordFile(Skipped.Path, Kind))
		{
			Block.NotHeld.Add(FMantlePlaceRecordNotHeld{ FamilyOf(Skipped.Path), Skipped.Path,
				Skipped.Reason.IsEmpty() ? FString(TEXT("the download holds no such file.")) : Skipped.Reason });
		}
	}

	FMantlePlacePublicRecordsLogic::OrderLayers(Block.Layers);
	Block.bPresent = Block.Layers.Num() > 0 || Block.NotHeld.Num() > 0;
	if (!Block.bPresent)
	{
		C.Skip(RecordsId, TEXT("This download holds no public-record layer."));
	}
}

void ReadLicensing(const FThreeContext& C)
{
	for (const FMantlePlaceBundleFile& File : C.Read.Files)
	{
		const TSharedPtr<FJsonObject> Entry = C.FindEntry(File.Path);
		if (!Entry.IsValid())
		{
			continue;
		}
		FMantlePlaceFileLicensing Terms;
		Terms.Role = MantlePlaceOdblLogic::ParseRole(Entry->HasField(TEXT("odbl_role")), GetString(Entry, TEXT("odbl_role")));
		Terms.License = GetString(Entry, TEXT("license"));
		const TArray<TSharedPtr<FJsonValue>>* Sources = nullptr;
		if (Entry->TryGetArrayField(TEXT("sources"), Sources))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Sources)
			{
				const TSharedPtr<FJsonObject> Source = Value.IsValid() ? Value->AsObject() : nullptr;
				if (Source.IsValid())
				{
					Terms.Sources.Add(FMantlePlaceFileSource{ GetString(Source, TEXT("name")), GetString(Source, TEXT("license")) });
				}
			}
		}
		C.M.FileLicensing.Add(File.EntryName, MoveTemp(Terms));
	}
}
}

const TArray<FString>& FMantlePlaceManifestThreeLogic::RequestedFiles()
{
	static const TArray<FString> Files = []()
	{
		TArray<FString> Out = {
			HeightmapPath(),
			DrapePath(),
			TerrainMeshPath(),
			BuildingsPath(),
			TreesPath(),
			RoadsPath(),
		};
		for (const TCHAR* Layer : { TEXT("Water"), TEXT("Grass"), TEXT("Forest"), TEXT("Dirt"),
				 TEXT("Rock"), TEXT("Sand"), TEXT("Snow"), TEXT("Built") })
		{
			Out.Add(FString(MaterialsFolder()) + Layer + TEXT(".png"));
		}
		for (const TCHAR* Record : { TEXT("Parcels"), TEXT("Zoning"), TEXT("Floodplain"), TEXT("Wetlands"),
				 TEXT("Soils"), TEXT("Heritage"), TEXT("Conservation"), TEXT("Hydrography"), TEXT("Contamination") })
		{
			Out.Add(FString(RecordsFolder()) + Record + TEXT(".dxf"));
		}
		return Out;
	}();
	return Files;
}

TArray<FString> FMantlePlaceManifestThreeLogic::ListedAmong(const FMantlePlaceBundleRead& Read, const TArray<FString>& Names)
{
	TArray<FString> Listed;
	for (const FString& Name : Names)
	{
		const bool bHeld = Read.Files.ContainsByPredicate(
			[&Name](const FMantlePlaceBundleFile& F) { return F.Path.Equals(Name, ESearchCase::IgnoreCase); });
		const bool bCatalogued = Read.Skipped.ContainsByPredicate([&Name](const FMantlePlaceBundleSkipped& S)
			{ return S.Why == EMantlePlaceBundleSkip::FileMissing && S.Path.Equals(Name, ESearchCase::IgnoreCase); });
		if (bHeld || bCatalogued)
		{
			Listed.Add(Name);
		}
	}
	return Listed;
}

FString FMantlePlaceManifestThreeLogic::NoHostReadyFileReason(const FString& What)
{
	return FString::Printf(TEXT("MPB 3.0 ships %s only as georeferenced files, and this plugin never converts a "
		"georeferenced file."), *What);
}

FString FMantlePlaceManifestThreeLogic::NotHeldReason(const FString& Path, const FString& StatedReason)
{
	return StatedReason.IsEmpty()
		? FString::Printf(TEXT("This download does not hold %s."), *Path)
		: FString::Printf(TEXT("This download does not hold %s: %s"), *Path, *StatedReason);
}

bool FMantlePlaceManifestThreeLogic::ChooseLandscapeLayout(int32 Width, int32 Height, FMantlePlaceLandscapeLayout& OutLayout)
{
	struct FCandidate { int32 Section; int32 Sections; };
	static const FCandidate Candidates[] = {
		{ 63, 2 }, { 63, 1 }, { 127, 2 }, { 127, 1 }, { 31, 2 }, { 31, 1 },
		{ 255, 2 }, { 255, 1 }, { 15, 2 }, { 15, 1 }, { 7, 2 }, { 7, 1 },
	};
	if (Width < 2 || Height < 2)
	{
		return false;
	}
	for (const FCandidate& C : Candidates)
	{
		const int32 Quads = C.Section * C.Sections;
		if ((Width - 1) % Quads != 0 || (Height - 1) % Quads != 0)
		{
			continue;
		}
		const int32 CountX = (Width - 1) / Quads;
		const int32 CountY = (Height - 1) / Quads;
		if (CountX < 1 || CountY < 1 || CountX > 32 || CountY > 32)
		{
			continue;
		}
		OutLayout = FMantlePlaceLandscapeLayout{ C.Section, C.Sections, CountX, CountY };
		return true;
	}
	return false;
}

bool FMantlePlaceManifestThreeLogic::ToVaultManifest(const FMantlePlaceBundleRead& Read, const FString& ManifestJson,
	FMantlePlaceVaultManifest& OutManifest, FString& OutError)
{
	OutManifest = FMantlePlaceVaultManifest();
	OutError.Reset();
	if (!Read.IsRead() || Read.Major < 3)
	{
		OutError = Read.Error.IsEmpty() ? FString(TEXT("Not an MPB 3.x bundle.")) : Read.Error;
		return false;
	}
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ManifestJson);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("Manifest.json is not a JSON object.");
		return false;
	}
	FMantlePlaceVaultManifest& M = OutManifest;
	M.bFormatThree = true;
	M.Version = Read.Version;

	if (const TSharedPtr<FJsonObject>* Order = GetObject(Root, TEXT("order")))
	{
		M.OrderId = GetString(*Order, TEXT("id"));
		if (const TSharedPtr<FJsonObject>* Aoi = GetObject(*Order, TEXT("aoi")))
		{
			M.Cesium.bHasBbox = true;
			M.Cesium.BboxWestDeg = GetNumber(*Aoi, TEXT("west"));
			M.Cesium.BboxSouthDeg = GetNumber(*Aoi, TEXT("south"));
			M.Cesium.BboxEastDeg = GetNumber(*Aoi, TEXT("east"));
			M.Cesium.BboxNorthDeg = GetNumber(*Aoi, TEXT("north"));
		}
	}
	M.JobId = M.OrderId;

	const TSharedPtr<FJsonObject>* Frames = GetObject(Root, TEXT("frames"));
	const TSharedPtr<FJsonObject>* Local = Frames != nullptr ? GetObject(*Frames, TEXT("local")) : nullptr;
	const TSharedPtr<FJsonObject>* Origin = Local != nullptr ? GetObject(*Local, TEXT("origin")) : nullptr;
	const TSharedPtr<FJsonObject>* Projected = Origin != nullptr ? GetObject(*Origin, TEXT("projected")) : nullptr;
	if (Projected == nullptr || !(*Projected)->HasTypedField<EJson::Number>(TEXT("x"))
		|| !(*Projected)->HasTypedField<EJson::Number>(TEXT("y")) || !(*Origin)->HasTypedField<EJson::Number>(TEXT("height_m")))
	{
		OutError = TEXT("Manifest.json states no site origin (frames.local.origin with its projected x and y and its "
			"height), so nothing in this bundle can be placed.");
		return false;
	}
	const TSharedPtr<FJsonObject>* LocalUnit = GetObject(*Local, TEXT("unit"));
	const TSharedPtr<FJsonObject>* OriginUnit = GetObject(*Projected, TEXT("unit"));
	if (!IsMetre(LocalUnit) || !IsMetre(OriginUnit))
	{
		OutError = FString::Printf(TEXT("The local site frame is stated in %s and its origin in %s; this plugin places "
			"a frame in metres only, and converts none."), *UnitWords(LocalUnit), *UnitWords(OriginUnit));
		return false;
	}
	if (const TSharedPtr<FJsonObject>* Axes = GetObject(*Local, TEXT("axes")))
	{
		const FString X = GetString(*Axes, TEXT("x")), Y = GetString(*Axes, TEXT("y")), Z = GetString(*Axes, TEXT("z"));
		if (X != TEXT("east") || Y != TEXT("north") || Z != TEXT("up"))
		{
			OutError = FString::Printf(TEXT("The local site frame's axes are x %s, y %s, z %s; this plugin knows only x "
				"east, y north, z up."), *X, *Y, *Z);
			return false;
		}
	}
	FMantlePlaceGeoreferenceBlock& G = M.Georef;
	if (const TSharedPtr<FJsonObject>* Crs = GetObject(*Projected, TEXT("crs")))
	{
		G.CrsProjected = GetString(*Crs, TEXT("code"));
		G.Epsg = MantlePlaceImportManifest::ParseEpsg(G.CrsProjected);
	}
	G.bHasOrigin = true;
	G.OriginEastingM = (*Projected)->GetNumberField(TEXT("x"));
	G.OriginNorthingM = (*Projected)->GetNumberField(TEXT("y"));
	G.GroundOrthometricHM = (*Origin)->GetNumberField(TEXT("height_m"));
	G.PlanetShape = TEXT("Flat");
	if ((*Origin)->HasTypedField<EJson::Number>(TEXT("longitude")) && (*Origin)->HasTypedField<EJson::Number>(TEXT("latitude")))
	{
		G.bHasGeographicOrigin = true;
		G.OriginLongitudeDeg = (*Origin)->GetNumberField(TEXT("longitude"));
		G.OriginLatitudeDeg = (*Origin)->GetNumberField(TEXT("latitude"));
	}
	if (const TSharedPtr<FJsonObject>* Datum = GetObject(*Local, TEXT("vertical_crs")))
	{
		M.SiteVerticalDatum = GetString(*Datum, TEXT("code"));
	}

	const TSharedPtr<FJsonObject>* Catalog = GetObject(Root, TEXT("catalog"));
	if (Catalog == nullptr)
	{
		OutError = TEXT("Manifest.json has no catalog.");
		return false;
	}
	const FThreeContext C{ Read, *Catalog, M };
	const double SiteHeightM = G.GroundOrthometricHM;
	ReadLicensing(C);
	ReadDrape(C);
	ReadHeightmap(C, SiteHeightM);
	ReadMeshes(C);
	ReadTrees(C, SiteHeightM);
	ReadRoads(C, SiteHeightM);
	if (M.Heightmap.bPresent)
	{
		ReadMaterials(C);
	}
	ReadRecords(C, Root);

	C.Skip(CoverageId, NoHostReadyFileReason(TEXT("the slope, aspect, hillshade, land-cover, canopy-height and NDVI "
		"layers")));

	M.bValid = true;
	return true;
}
