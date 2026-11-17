// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceBundleReaderLogic.h"

#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion, FMantlePlaceManifestVersion

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
/** The names the served 3.0.0 manifest schema gives the catalog and the statements beside it; a 2.x
 *  manifest keeps its catalog in `file_entries`. */
const TCHAR* const CatalogKey3 = TEXT("catalog");
const TCHAR* const CatalogKey2 = TEXT("file_entries");
const TCHAR* const LevelKey = TEXT("level");
const TCHAR* const NotHeldKey = TEXT("not_held");
const TCHAR* const CountKey = TEXT("count");

/** The ordinal order a reader in any language sorts paths in: by code unit, case included. */
template <typename RowType>
void SortByPath(TArray<RowType>& Rows)
{
	Rows.Sort([](const RowType& A, const RowType& B)
	{
		return A.Path.Compare(B.Path, ESearchCase::CaseSensitive) < 0;
	});
}

/** The last path segment's extension, lowercased; empty when the file name has no dot. */
FString ExtensionOf(const FString& Path)
{
	int32 Slash = INDEX_NONE;
	Path.FindLastChar(TEXT('/'), Slash);
	const FString Name = Slash == INDEX_NONE ? Path : Path.Mid(Slash + 1);
	int32 Dot = INDEX_NONE;
	return Name.FindLastChar(TEXT('.'), Dot) ? Name.Mid(Dot + 1).ToLower() : FString();
}

/** A count the catalog states: the `value` of the entry's `count` object, a JSON number that is a whole
 *  number, not negative. Nothing else is a count. */
bool ReadCount(const TSharedPtr<FJsonObject>& Entry, int64& OutCount)
{
	const TSharedPtr<FJsonValue> Counted = Entry.IsValid() ? Entry->TryGetField(CountKey) : nullptr;
	const TSharedPtr<FJsonObject>* CountObject = nullptr;
	if (!Counted.IsValid() || Counted->Type != EJson::Object || !Counted->TryGetObject(CountObject))
	{
		return false;
	}
	const TSharedPtr<FJsonValue> Value = (*CountObject)->TryGetField(TEXT("value"));
	if (!Value.IsValid() || Value->Type != EJson::Number)
	{
		return false;
	}
	const double Number = Value->AsNumber();
	if (Number < 0.0 || FMath::Frac(Number) != 0.0 || Number > 9007199254740992.0)
	{
		return false;
	}
	OutCount = static_cast<int64>(Number);
	return true;
}

/** A member that is a string, and only a string: the engine's TryGetStringField also turns a number into one. */
bool ReadString(const TSharedPtr<FJsonObject>& Object, const TCHAR* Key, FString& Out)
{
	const TSharedPtr<FJsonValue> Value = Object.IsValid() ? Object->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::String)
	{
		return false;
	}
	Out = Value->AsString();
	return true;
}

const TSharedPtr<FJsonObject>* ReadObject(const TSharedPtr<FJsonObject>& Object, const TCHAR* Key)
{
	const TSharedPtr<FJsonObject>* Out = nullptr;
	const TSharedPtr<FJsonValue> Value = Object.IsValid() ? Object->TryGetField(Key) : nullptr;
	return Value.IsValid() && Value->Type == EJson::Object && Value->TryGetObject(Out) ? Out : nullptr;
}

// The frames' reading, as ManifestFramesReader.cs reads them: a unit is known by its factor to metres.
constexpr double FactorTolerance = 1e-9;
constexpr double InternationalFootToMetres = 0.3048;
constexpr double UsSurveyFootToMetres = 1200.0 / 3937.0;

bool NearFactor(double Stated, double Known)
{
	return FMath::Abs(Stated - Known) <= Known * FactorTolerance;
}

/** A stated value as a message names it: a string in quotes, a number as written. */
FString Raw(const TSharedPtr<FJsonValue>& Value)
{
	if (!Value.IsValid() || Value->Type == EJson::Null)
	{
		return TEXT("null");
	}
	switch (Value->Type)
	{
		case EJson::String:  return TEXT("\"") + Value->AsString() + TEXT("\"");
		case EJson::Number:  return FString::SanitizeFloat(Value->AsNumber());
		case EJson::Boolean: return Value->AsBool() ? TEXT("true") : TEXT("false");
		default:             return TEXT("(a structure)");
	}
}

bool RefuseFrames(FString& Error, const FString& Message)
{
	Error = Message;
	return false;
}

const TSharedPtr<FJsonObject>* FramesMember(
	const TSharedPtr<FJsonObject>& Parent, const TCHAR* Name, const FString& Where, FString& Error)
{
	const TSharedPtr<FJsonObject>* Object = ReadObject(Parent, Name);
	if (Object == nullptr)
	{
		Error = Parent.IsValid() && Parent->HasField(Name)
			? FString::Printf(TEXT("Manifest.json's %s.%s is not an object."), *Where, Name)
			: FString::Printf(TEXT("Manifest.json states no %s.%s, which a reader of its frames needs."), *Where, Name);
	}
	return Object;
}

/** A stated number; false, with the refusal, when absent or not a finite number. */
bool FramesNumber(const TSharedPtr<FJsonObject>& Parent, const TCHAR* Name, const FString& Where, FString& Error)
{
	const TSharedPtr<FJsonValue> Value = Parent->TryGetField(Name);
	if (!Value.IsValid())
	{
		return RefuseFrames(Error, FString::Printf(
			TEXT("Manifest.json states no %s.%s, which a reader of its frames needs."), *Where, Name));
	}
	if (Value->Type != EJson::Number || !FMath::IsFinite(Value->AsNumber()))
	{
		return RefuseFrames(Error, FString::Printf(
			TEXT("Manifest.json's %s.%s states %s, which is not a number."), *Where, Name, *Raw(Value)));
	}
	return true;
}

/** A linear unit: a name, and a factor that is the metre, the international foot or the US survey foot. */
bool FramesUnit(const TSharedPtr<FJsonObject>& Parent, const TCHAR* Name, const FString& Where, bool bMustBeMetre,
	FString& Error)
{
	const FString At = Where + TEXT(".") + Name;
	const TSharedPtr<FJsonObject>* Unit = FramesMember(Parent, Name, Where, Error);
	if (Unit == nullptr)
	{
		return false;
	}
	FString UnitName;
	ReadString(*Unit, TEXT("name"), UnitName);
	if (UnitName.IsEmpty())
	{
		return RefuseFrames(Error, FString::Printf(TEXT("Manifest.json's %s states no name for its unit."), *At));
	}
	const TSharedPtr<FJsonValue> Factor = (*Unit)->TryGetField(TEXT("to_metres"));
	if (!Factor.IsValid() || Factor->Type != EJson::Number)
	{
		return RefuseFrames(Error, FString::Printf(
			TEXT("Manifest.json's %s (%s) states no to_metres factor, so its unit is not known."), *At, *UnitName));
	}
	const double ToMetres = Factor->AsNumber();
	const bool bPositive = FMath::IsFinite(ToMetres) && ToMetres > 0.0;
	const bool bMetre = bPositive && NearFactor(ToMetres, 1.0);
	const bool bKnown = bMetre
		|| (bPositive && (NearFactor(ToMetres, InternationalFootToMetres) || NearFactor(ToMetres, UsSurveyFootToMetres)));
	if (!bKnown)
	{
		return RefuseFrames(Error, FString::Printf(
			TEXT("Manifest.json's %s states the unit %s (%s metres), which this plugin does not understand: "
			     "it reads the metre, the international foot and the US survey foot."),
			*At, *UnitName, *Raw(Factor)));
	}
	if (bMustBeMetre && !bMetre)
	{
		return RefuseFrames(Error, FString::Printf(
			TEXT("Manifest.json states the local site frame's unit as %s (%s metres), but that frame is in metres on every order."),
			*UnitName, *Raw(Factor)));
	}
	return true;
}

/** An authority code as the served schema words it: `EPSG:32618`. */
bool IsAuthorityCode(const FString& Code)
{
	int32 Colon = INDEX_NONE;
	if (!Code.FindChar(TEXT(':'), Colon) || Colon == 0 || Colon == Code.Len() - 1)
	{
		return false;
	}
	for (int32 I = 0; I < Code.Len(); ++I)
	{
		const TCHAR C = Code[I];
		if (I == Colon)
		{
			continue;
		}
		const bool bUpper = C >= TEXT('A') && C <= TEXT('Z');
		const bool bRest = bUpper || (C >= TEXT('a') && C <= TEXT('z')) || (C >= TEXT('0') && C <= TEXT('9')) || C == TEXT('.');
		if (I < Colon ? !bUpper : !bRest)
		{
			return false;
		}
	}
	return true;
}

/** A CRS as the manifest names it: whole, as a code, a PROJJSON object or a WKT2 string. */
bool FramesCrs(const TSharedPtr<FJsonObject>& Parent, const TCHAR* Name, const FString& Where, FString& Error)
{
	const FString At = Where + TEXT(".") + Name;
	const TSharedPtr<FJsonObject>* Crs = FramesMember(Parent, Name, Where, Error);
	if (Crs == nullptr)
	{
		return false;
	}
	bool bNamed = false;
	if ((*Crs)->HasField(TEXT("code")))
	{
		FString Code;
		if (!ReadString(*Crs, TEXT("code"), Code) || !IsAuthorityCode(Code))
		{
			return RefuseFrames(Error, FString::Printf(
				TEXT("Manifest.json's %s names the code %s, which is not an authority code such as EPSG:32618."),
				*At, *Raw((*Crs)->TryGetField(TEXT("code")))));
		}
		bNamed = true;
	}
	if ((*Crs)->HasField(TEXT("projjson")))
	{
		if (ReadObject(*Crs, TEXT("projjson")) == nullptr)
		{
			return RefuseFrames(Error, FString::Printf(TEXT("Manifest.json's %s.projjson is not a PROJJSON object."), *At));
		}
		bNamed = true;
	}
	if ((*Crs)->HasField(TEXT("wkt2")))
	{
		FString Wkt;
		if (!ReadString(*Crs, TEXT("wkt2"), Wkt) || Wkt.IsEmpty())
		{
			return RefuseFrames(Error, FString::Printf(TEXT("Manifest.json's %s.wkt2 is not a WKT2 string."), *At));
		}
		bNamed = true;
	}
	return bNamed ? true : RefuseFrames(Error, FString::Printf(
		TEXT("Manifest.json's %s names no code, PROJJSON object or WKT2 string, so it names no CRS."), *At));
}

/** `geoid_model` is a name or null; absent is stated as null, so every read has the same shape. */
bool FramesGeoid(const TSharedPtr<FJsonObject>& Frame, const FString& Where, FString& Error)
{
	const TSharedPtr<FJsonValue> Value = Frame->TryGetField(TEXT("geoid_model"));
	if (!Value.IsValid() || Value->Type == EJson::Null)
	{
		Frame->SetField(TEXT("geoid_model"), MakeShared<FJsonValueNull>());
		return true;
	}
	return Value->Type == EJson::String
		? true
		: RefuseFrames(Error, FString::Printf(TEXT("Manifest.json's %s.geoid_model is not a name or null."), *Where));
}
} // namespace

bool FMantlePlaceBundleReaderLogic::ReadFrames(
	const TSharedPtr<FJsonObject>& Manifest, TSharedPtr<FJsonObject>& OutFrames, FString& OutError)
{
	OutFrames.Reset();
	const TSharedPtr<FJsonObject>* Frames = ReadObject(Manifest, TEXT("frames"));
	if (Frames == nullptr)
	{
		return RefuseFrames(OutError, Manifest.IsValid() && Manifest->HasField(TEXT("frames"))
			? FString(TEXT("Manifest.json's frames is not an object."))
			: FString(TEXT("Manifest.json states no frames, which a reader needs to place any file (spec/format.md §9.4).")));
	}
	const TSharedPtr<FJsonObject>* Georeferenced = FramesMember(*Frames, TEXT("georeferenced"), TEXT("frames"), OutError);
	const TSharedPtr<FJsonObject>* Local = Georeferenced ? FramesMember(*Frames, TEXT("local"), TEXT("frames"), OutError) : nullptr;
	if (Georeferenced == nullptr || Local == nullptr)
	{
		return false;
	}

	// The georeferenced frame.
	const FString Order = TEXT("frames.georeferenced");
	if (!FramesCrs(*Georeferenced, TEXT("horizontal_crs"), Order, OutError)
		|| !FramesCrs(*Georeferenced, TEXT("vertical_crs"), Order, OutError)
		|| !FramesGeoid(*Georeferenced, Order, OutError)
		|| !FramesUnit(*Georeferenced, TEXT("horizontal_unit"), Order, /*bMustBeMetre=*/false, OutError)
		|| !FramesUnit(*Georeferenced, TEXT("vertical_unit"), Order, /*bMustBeMetre=*/false, OutError))
	{
		return false;
	}
	FString AxisOrder = TEXT("(absent)");
	ReadString(*Georeferenced, TEXT("axis_order"), AxisOrder);
	if (AxisOrder != TEXT("east-north") && AxisOrder != TEXT("north-east"))
	{
		return RefuseFrames(OutError, FString::Printf(
			TEXT("Manifest.json's %s.axis_order states %s, which is not east-north or north-east."), *Order, *AxisOrder));
	}
	if (!FramesNumber(*Georeferenced, TEXT("grid_rotation_deg"), Order, OutError))
	{
		return false;
	}

	// The local site frame.
	const FString Site = TEXT("frames.local");
	const FString OriginAt = Site + TEXT(".origin");
	const FString ProjectedAt = OriginAt + TEXT(".projected");
	const TSharedPtr<FJsonObject>* Origin = FramesMember(*Local, TEXT("origin"), Site, OutError);
	const TSharedPtr<FJsonObject>* Projected = Origin ? FramesMember(*Origin, TEXT("projected"), OriginAt, OutError) : nullptr;
	if (Origin == nullptr || Projected == nullptr)
	{
		return false;
	}
	for (const TCHAR* Name : { TEXT("longitude"), TEXT("latitude"), TEXT("ellipsoidal_height_m"), TEXT("height_m") })
	{
		if (!FramesNumber(*Origin, Name, OriginAt, OutError))
		{
			return false;
		}
	}
	if (!FramesCrs(*Projected, TEXT("crs"), ProjectedAt, OutError)
		|| !FramesNumber(*Projected, TEXT("x"), ProjectedAt, OutError)
		|| !FramesNumber(*Projected, TEXT("y"), ProjectedAt, OutError)
		|| !FramesUnit(*Projected, TEXT("unit"), ProjectedAt, /*bMustBeMetre=*/false, OutError)
		|| !FramesCrs(*Local, TEXT("vertical_crs"), Site, OutError)
		|| !FramesUnit(*Local, TEXT("unit"), Site, /*bMustBeMetre=*/false, OutError))
	{
		return false;
	}
	const TSharedPtr<FJsonObject>* Axes = FramesMember(*Local, TEXT("axes"), Site, OutError);
	if (Axes == nullptr)
	{
		return false;
	}
	struct FAxis
	{
		const TCHAR* Name;
		const TCHAR* Direction;
	};
	for (const FAxis& Axis : { FAxis{ TEXT("x"), TEXT("east") }, FAxis{ TEXT("y"), TEXT("north") }, FAxis{ TEXT("z"), TEXT("up") } })
	{
		FString Stated = TEXT("(absent)");
		ReadString(*Axes, Axis.Name, Stated);
		if (Stated != Axis.Direction)
		{
			return RefuseFrames(OutError, FString::Printf(
				TEXT("Manifest.json's %s.axes states the %s axis as %s, which is not %s: the local site frame runs east, north, up."),
				*Site, Axis.Name, *Stated, Axis.Direction));
		}
	}
	// The local frame is in metres on every order: any other unit is refused, never converted.
	if (!FramesUnit(*Local, TEXT("unit"), Site, /*bMustBeMetre=*/true, OutError)
		|| !FramesGeoid(*Local, Site, OutError)
		|| !FramesNumber(*Local, TEXT("grid_rotation_deg"), Site, OutError))
	{
		return false;
	}
	OutFrames = *Frames;
	return true;
}

bool FMantlePlaceBundleReaderLogic::IsReadableEntry(const FString& EntryName)
{
	if (EntryName.IsEmpty() || EntryName.EndsWith(TEXT("/")) || EntryName.StartsWith(TEXT("/"))
		|| EntryName.Contains(TEXT("\\")) || EntryName.Contains(TEXT(":")))
	{
		return false;
	}
	TArray<FString> Parts;
	EntryName.ParseIntoArray(Parts, TEXT("/"), /*InCullEmpty=*/false);
	for (const FString& Part : Parts)
	{
		if (Part.IsEmpty() || Part == TEXT(".") || Part == TEXT(".."))
		{
			return false;
		}
	}
	// Archive tools' own metadata, which is not the bundle's: a macOS zip's resource-fork folder and
	// Finder's folder settings.
	return Parts[0] != TEXT("__MACOSX") && Parts.Last() != TEXT(".DS_Store");
}

bool FMantlePlaceBundleReaderLogic::FindManifest(
	const TArray<FString>& EntryNames, FString& OutRoot, FString& OutManifestPath)
{
	OutRoot.Reset();
	OutManifestPath.Reset();

	// FString's hash and equality ignore case, so the manifest's name is matched regardless of case.
	TSet<FString> Entries;
	TSet<FString> Tops;
	bool bAllInFolders = true;
	for (const FString& Name : EntryNames)
	{
		if (!IsReadableEntry(Name))
		{
			continue;
		}
		Entries.Add(Name);
		FString Top;
		bAllInFolders &= Name.Split(TEXT("/"), &Top, nullptr);
		Tops.Add(bAllInFolders ? Top : Name);
	}

	for (const TCHAR* Manifest : { ManifestPath3(), ManifestPath2() })
	{
		if (Entries.Contains(Manifest))
		{
			OutManifestPath = Manifest;
			return true;
		}
	}
	if (bAllInFolders && Tops.Num() == 1)
	{
		const FString Wrapper = *Tops.CreateConstIterator() + TEXT("/");
		for (const TCHAR* Manifest : { ManifestPath3(), ManifestPath2() })
		{
			if (Entries.Contains(Wrapper + Manifest))
			{
				OutRoot = Wrapper;
				OutManifestPath = Manifest;
				return true;
			}
		}
	}
	return false;
}

FMantlePlaceBundleRead FMantlePlaceBundleReaderLogic::Read(const TArray<FString>& EntryNames, FReadText ReadText)
{
	FMantlePlaceBundleRead Out;
	if (!FindManifest(EntryNames, Out.Root, Out.ManifestPath))
	{
		Out.Error = FString::Printf(
			TEXT("This is not a Mantle Place bundle: it has no %s (MPB 3.x) or %s (MPB 2.x and 1.x) at its top, "
			     "or inside one folder."),
			ManifestPath3(), ManifestPath2());
		return Out;
	}

	// Every entry under the bundle's root, by its path from the root, at the archive's own spelling.
	// The map's keys ignore case, which is how a catalog name finds its entry.
	TMap<FString, FString> Held;
	for (const FString& Name : EntryNames)
	{
		// FString::StartsWith is false for an empty prefix, so a bundle at the top is said in so many words.
		if (IsReadableEntry(Name) && (Out.Root.IsEmpty() || Name.StartsWith(Out.Root)))
		{
			Held.Add(Name.Mid(Out.Root.Len()), Name);
		}
	}

	FString ManifestText;
	TSharedPtr<FJsonObject> Manifest;
	const TSharedRef<TJsonReader<TCHAR>> JsonReader = TJsonReaderFactory<TCHAR>::Create(
		ReadText(Held.FindChecked(Out.ManifestPath), ManifestText) ? ManifestText : FString());
	if (!FJsonSerializer::Deserialize(JsonReader, Manifest) || !Manifest.IsValid())
	{
		Out.Error = FString::Printf(TEXT("The bundle's %s could not be read as JSON."), *Out.ManifestPath);
		return Out;
	}

	// The version, and nothing else, before the gate (format.md §14). A STRING, parsed as semver: an
	// integer from the pre-history is not a version this reader speaks (HPS-31).
	ReadString(Manifest, TEXT("version"), Out.Version);
	const FMantlePlaceManifestVersion Parsed = FMantlePlaceManifestVersion::Parse(Out.Version);
	if (!Parsed.bValid || Parsed < FMantlePlaceManifestVersion::Parse(MantlePlaceMinSupportedManifestVersion))
	{
		Out.Error = BelowFloorMessage(Out.Version, MantlePlaceMinSupportedManifestVersion);
		return Out;
	}
	Out.Major = Parsed.Major;
	if (Parsed.Major > NewestMajor)
	{
		Out.Outcome = EMantlePlaceBundleReadOutcome::Refused;
		Out.Refusal = MajorAheadRefusal();
		Out.Error = NewerMajorMessage(Out.Version, NewestMajor);
		return Out;
	}

	// Each layout keeps its manifest in one place (compatibility.md §1); one that is elsewhere is not a
	// bundle of either.
	const bool bThree = Parsed.Major >= 3;
	if (bThree != (Out.ManifestPath == ManifestPath3()))
	{
		Out.Error = FString::Printf(
			TEXT("The bundle's manifest is at %s and declares version %s; an MPB %s manifest is at %s."),
			*Out.ManifestPath, *Out.Version, bThree ? TEXT("3.x") : TEXT("2.x or 1.x"),
			bThree ? ManifestPath3() : ManifestPath2());
		return Out;
	}

	// A 3.x manifest's two frames, stated once. A unit, datum, axis order or frame this reader does not
	// understand refuses the manifest, naming the value (format.md §4); nothing is converted or derived.
	if (bThree)
	{
		FString FramesError;
		if (!ReadFrames(Manifest, Out.Frames, FramesError))
		{
			Out.Outcome = EMantlePlaceBundleReadOutcome::Refused;
			Out.Refusal = UnreadableRefusal();
			Out.Error = FramesError;
			return Out;
		}
	}

	// The download's level, as the manifest states it once. 2.x states none.
	if (bThree)
	{
		ReadString(Manifest, LevelKey, Out.Level);
	}

	// The one catalog. A per-host block is never consulted: this reads the catalog and nothing under
	// `hosts`, in either major.
	const TCHAR* const CatalogKey = bThree ? CatalogKey3 : CatalogKey2;
	const TSharedPtr<FJsonObject>* Catalog = ReadObject(Manifest, CatalogKey);
	if (Catalog == nullptr)
	{
		Out.Error = FString::Printf(TEXT("The bundle's %s has no `%s` catalog to find its files in."),
			*Out.ManifestPath, CatalogKey);
		return Out;
	}
	TSet<FString> Named;
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*Catalog)->Values)
	{
		Named.Add(Pair.Key);
		const FString* Entry = Held.Find(Pair.Key);
		if (Entry == nullptr)
		{
			Out.Skipped.Add({ Pair.Key, EMantlePlaceBundleSkip::FileMissing, FString() });
			continue;
		}
		const TSharedPtr<FJsonObject>* EntryObject = nullptr;
		const TSharedPtr<FJsonObject> Statement =
			Pair.Value.IsValid() && Pair.Value->Type == EJson::Object && Pair.Value->TryGetObject(EntryObject)
				? *EntryObject : nullptr;

		FMantlePlaceBundleFile File;
		File.Path = Pair.Key;
		File.EntryName = *Entry;
		if (bThree)
		{
			File.Family = FamilyOfExtension(Pair.Key);
			File.bHasCount = ReadCount(Statement, File.Count);
		}
		else
		{
			FString Mode;
			if (const TSharedPtr<FJsonObject>* Frame = ReadObject(Statement, TEXT("frame")))
			{
				ReadString(*Frame, TEXT("coordinate_mode"), Mode);
			}
			File.Family = FamilyOfCoordinateMode(Mode);
		}
		File.Frame = FrameOf(File.Family);
		File.Unit = UnitOf(File.Family);
		Out.Files.Add(MoveTemp(File));
	}

	// The promised files this download does not hold, each with the reason the manifest states,
	// verbatim: a reader shows it and never matches on it (format.md §9.6).
	// The list is `{path, reason}` rows. A row with no path is no statement about a file.
	const TSharedPtr<FJsonValue> NotHeldValue = bThree ? Manifest->TryGetField(NotHeldKey) : nullptr;
	if (NotHeldValue.IsValid() && NotHeldValue->Type == EJson::Array)
	{
		for (const TSharedPtr<FJsonValue>& Row : NotHeldValue->AsArray())
		{
			const TSharedPtr<FJsonObject>* Statement = nullptr;
			FString Path;
			if (!Row.IsValid() || Row->Type != EJson::Object || !Row->TryGetObject(Statement)
				|| !ReadString(*Statement, TEXT("path"), Path) || Named.Contains(Path))
			{
				continue; // the catalog's word on a file it names stands
			}
			FString Reason;
			ReadString(*Statement, TEXT("reason"), Reason);
			Out.Skipped.Add({ Path, EMantlePlaceBundleSkip::NotHeld, Reason });
		}
	}

	SortByPath(Out.Files);
	SortByPath(Out.Skipped);
	Out.Outcome = EMantlePlaceBundleReadOutcome::Read;
	return Out;
}

EMantlePlaceBundleFamily FMantlePlaceBundleReaderLogic::FamilyOfExtension(const FString& Path)
{
	static const TSet<FString> Georeferenced = { TEXT("tif"), TEXT("gpkg"), TEXT("dxf"), TEXT("landxml"), TEXT("ifc") };
	static const TSet<FString> HostReady = { TEXT("glb"), TEXT("usdz"), TEXT("obj"), TEXT("png"), TEXT("csv") };
	const FString Extension = ExtensionOf(Path);
	if (Extension.IsEmpty())
	{
		return EMantlePlaceBundleFamily::None;
	}
	if (Georeferenced.Contains(Extension))
	{
		return EMantlePlaceBundleFamily::Georeferenced;
	}
	return HostReady.Contains(Extension) ? EMantlePlaceBundleFamily::HostReady : EMantlePlaceBundleFamily::None;
}

EMantlePlaceBundleFamily FMantlePlaceBundleReaderLogic::FamilyOfCoordinateMode(const FString& CoordinateMode)
{
	if (CoordinateMode.Equals(TEXT("absolute"), ESearchCase::CaseSensitive))
	{
		return EMantlePlaceBundleFamily::Georeferenced;
	}
	return CoordinateMode.Equals(TEXT("offset"), ESearchCase::CaseSensitive)
		? EMantlePlaceBundleFamily::HostReady : EMantlePlaceBundleFamily::None;
}

EMantlePlaceBundleFrame FMantlePlaceBundleReaderLogic::FrameOf(EMantlePlaceBundleFamily Family)
{
	switch (Family)
	{
		case EMantlePlaceBundleFamily::Georeferenced: return EMantlePlaceBundleFrame::Order;
		case EMantlePlaceBundleFamily::HostReady:     return EMantlePlaceBundleFrame::Site;
		default:                                      return EMantlePlaceBundleFrame::None;
	}
}

EMantlePlaceBundleUnit FMantlePlaceBundleReaderLogic::UnitOf(EMantlePlaceBundleFamily Family)
{
	switch (Family)
	{
		case EMantlePlaceBundleFamily::Georeferenced: return EMantlePlaceBundleUnit::OrderDelivery;
		case EMantlePlaceBundleFamily::HostReady:     return EMantlePlaceBundleUnit::Metres;
		default:                                      return EMantlePlaceBundleUnit::None;
	}
}

const TCHAR* FMantlePlaceBundleReaderLogic::FamilyName(EMantlePlaceBundleFamily Family)
{
	switch (Family)
	{
		case EMantlePlaceBundleFamily::Georeferenced: return TEXT("georeferenced");
		case EMantlePlaceBundleFamily::HostReady:     return TEXT("host-ready");
		default:                                      return TEXT("");
	}
}

const TCHAR* FMantlePlaceBundleReaderLogic::FrameName(EMantlePlaceBundleFrame Frame)
{
	switch (Frame)
	{
		case EMantlePlaceBundleFrame::Order: return TEXT("order");
		case EMantlePlaceBundleFrame::Site:  return TEXT("site");
		default:                             return TEXT("");
	}
}

const TCHAR* FMantlePlaceBundleReaderLogic::SkipName(EMantlePlaceBundleSkip Why)
{
	return Why == EMantlePlaceBundleSkip::NotHeld ? TEXT("not-held") : TEXT("file-missing");
}

FString FMantlePlaceBundleReaderLogic::NewerMajorMessage(const FString& Version, int32 ReadsUpToMajor)
{
	return FString::Printf(
	    TEXT("Bundle manifest version %s is newer than this plugin understands (it reads up to %d.x). "
	         "Update the Mantle Place plugin to import this bundle."),
	    *Version, ReadsUpToMajor);
}

FString FMantlePlaceBundleReaderLogic::BelowFloorMessage(const FString& Version, const FString& Floor)
{
	return FString::Printf(
	    TEXT("Bundle manifest version %s is no longer supported (minimum %s). Re-download this "
	         "AOI from your vault at mantle.place/vault — rebuilding it there re-cuts the bundle "
	         "on the current pipeline."),
	    Version.IsEmpty() ? TEXT("(absent)") : *Version, *Floor);
}
