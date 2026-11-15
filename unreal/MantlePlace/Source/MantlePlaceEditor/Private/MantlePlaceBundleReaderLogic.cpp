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
} // namespace

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
