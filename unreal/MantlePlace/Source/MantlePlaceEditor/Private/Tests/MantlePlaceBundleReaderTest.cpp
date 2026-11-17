// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceBundleArchive.h"
#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceVaultTypes.h" // MantlePlaceMaxSupportedManifestMajor
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "FileUtilities/ZipArchiveWriter.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonWriter.h"

// The bundle reader, of either layout: `MantlePlace.Import.ReaderCorpus` runs every case of the
// conformance corpus's `reader` group against it, each bundle built three ways (a folder, a zip, a zip
// inside one wrapper folder) and opened the way the plugin opens one (MantlePlaceBundleArchive).
// `MantlePlace.Import.BundleReaderLogic` holds the rules no corpus case states on their own: the
// wrapper folder, the entries the reader ignores, the family table, the major gate's message, and that
// no 3.x file is found through a per-host block.

namespace
{
using namespace MantlePlaceConformanceCorpus;

/** A set of archive entries with fixed text, read through the logic unit with no archive at all. */
struct FInMemoryBundle
{
	TMap<FString, FString> Entries;

	FMantlePlaceBundleRead Read() const
	{
		TArray<FString> Names;
		Entries.GetKeys(Names);
		return FMantlePlaceBundleReaderLogic::Read(Names, [this](const FString& Name, FString& OutText)
		{
			const FString* Found = Entries.Find(Name);
			if (Found == nullptr)
			{
				return false;
			}
			OutText = *Found;
			return true;
		});
	}
};

const FMantlePlaceBundleFile* FindFile(const FMantlePlaceBundleRead& Read, const FString& Path)
{
	return Read.Files.FindByPredicate([&Path](const FMantlePlaceBundleFile& F) { return F.Path == Path; });
}

// --- The corpus ------------------------------------------------------------------------------------

/** One file's bytes as the corpus describes them: a string as UTF-8, any JSON value as JSON. */
TArray<uint8> BytesOfValue(const TSharedPtr<FJsonValue>& Value)
{
	FString Text;
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (Value.IsValid() && Value->Type == EJson::Object && Value->TryGetObject(Object) && Object != nullptr)
	{
		// A JSON object is a manifest, or a `$geopackage` description: the reader lists a GeoPackage and
		// never opens it, so the description's own text stands in for the database's bytes.
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
		FJsonSerializer::Serialize(Object->ToSharedRef(), Writer);
		Text += TEXT("\n");
	}
	else if (Value.IsValid())
	{
		Text = Value->AsString(); // a string, as written; the corpus describes no other file content
	}
	const FTCHARToUTF8 Utf8(*Text);
	return TArray<uint8>(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
}

enum class EForm : uint8
{
	Folder,
	Zip,
	Wrapped,
};

const TCHAR* FormName(EForm Form)
{
	switch (Form)
	{
		case EForm::Folder: return TEXT("folder");
		case EForm::Zip:    return TEXT("zip");
		default:            return TEXT("wrapped zip");
	}
}

/** Whether two of the bundle's paths, or the folders above them, differ only by case: such a bundle
 *  is built as the two zips only, since no case-insensitive file system can hold it as a folder. */
bool HasCaseCollision(const TMap<FString, TArray<uint8>>& Files)
{
	TSet<FString> Exact;
	for (const TPair<FString, TArray<uint8>>& Pair : Files)
	{
		TArray<FString> Parts;
		Pair.Key.ParseIntoArray(Parts, TEXT("/"));
		FString Prefix;
		for (const FString& Part : Parts)
		{
			Prefix = Prefix.IsEmpty() ? Part : Prefix + TEXT("/") + Part;
			Exact.Add(Prefix.ToLower() + TEXT("|") + Prefix);
		}
	}
	TSet<FString> Folded;
	for (const FString& Entry : Exact)
	{
		FString Lower;
		Entry.Split(TEXT("|"), &Lower, nullptr);
		Folded.Add(Lower);
	}
	return Folded.Num() < Exact.Num();
}

/** Build the bundle in `Form` under `Dir`; returns the path to hand the reader, or empty on failure. */
FString Build(EForm Form, const TMap<FString, TArray<uint8>>& Files, const FString& Dir)
{
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	if (Form == EForm::Folder)
	{
		const FString Root = FPaths::Combine(Dir, TEXT("bundle"));
		for (const TPair<FString, TArray<uint8>>& Pair : Files)
		{
			const FString Target = FPaths::Combine(Root, Pair.Key);
			Platform.CreateDirectoryTree(*FPaths::GetPath(Target));
			if (!FFileHelper::SaveArrayToFile(Pair.Value, *Target))
			{
				return FString();
			}
		}
		return Root;
	}
	const FString ZipPath = FPaths::Combine(Dir, Form == EForm::Wrapped ? TEXT("wrapped.zip") : TEXT("zip.zip"));
	Platform.CreateDirectoryTree(*Dir);
	IFileHandle* Out = Platform.OpenWrite(*ZipPath);
	if (Out == nullptr)
	{
		return FString();
	}
	{
		FZipArchiveWriter Writer(Out); // takes ownership of the handle; the destructor writes the directory
		const FDateTime Now = FDateTime::UtcNow();
		const FString Prefix = Form == EForm::Wrapped ? TEXT("site-bundle/") : TEXT("");
		for (const TPair<FString, TArray<uint8>>& Pair : Files)
		{
			Writer.AddFile(Prefix + Pair.Key, Pair.Value, Now);
		}
	}
	return ZipPath;
}

/** The corpus's string for a value that may be null: empty for null. */
bool ReadNullableString(const TSharedPtr<FJsonObject>& Object, const TCHAR* Key, FString& Out)
{
	const TSharedPtr<FJsonValue> Value = Object->TryGetField(Key);
	if (!Value.IsValid())
	{
		return false;
	}
	if (Value->Type == EJson::Null)
	{
		Out.Reset();
		return true;
	}
	if (Value->Type != EJson::String)
	{
		return false;
	}
	Out = Value->AsString();
	return true;
}

/** One found file as the corpus states it: `family|frame|count`, nulls written as `null`. */
FString DescribeFile(const FString& Family, const FString& Frame, const FString& Count)
{
	return FString::Printf(TEXT("family=%s frame=%s count=%s"),
		Family.IsEmpty() ? TEXT("null") : *Family, Frame.IsEmpty() ? TEXT("null") : *Frame, *Count);
}

/** The case's `result` against what the reader found, member by member; one failure per difference. */
void CompareResult(FAutomationTestBase& T, const FString& Label, const TSharedPtr<FJsonObject>& Result,
                   const FMantlePlaceBundleRead& Read)
{
	FString Outcome;
	Result->TryGetStringField(TEXT("outcome"), Outcome);
	const bool bRefused = Outcome == TEXT("refused");
	T.TestEqual(Label + TEXT("outcome"),
		Read.Outcome == EMantlePlaceBundleReadOutcome::Read ? TEXT("read")
		: Read.Outcome == EMantlePlaceBundleReadOutcome::Refused ? TEXT("refused") : TEXT("failed"),
		Outcome);
	if (Read.Outcome == EMantlePlaceBundleReadOutcome::Failed)
	{
		T.AddError(Label + TEXT("the reader failed: ") + Read.Error);
	}

	int32 Major = 0;
	if (T.TestTrue(Label + TEXT("the case states `major`"), Result->TryGetNumberField(TEXT("major"), Major)))
	{
		T.TestEqual(Label + TEXT("major"), Read.Major, Major);
	}
	FString Version, ManifestPath, Level, Refusal;
	if (T.TestTrue(Label + TEXT("the case states `version`"), Result->TryGetStringField(TEXT("version"), Version)))
	{
		T.TestEqual(Label + TEXT("version"), Read.Version, Version);
	}
	if (T.TestTrue(Label + TEXT("the case states `manifestPath`"),
		    Result->TryGetStringField(TEXT("manifestPath"), ManifestPath)))
	{
		T.TestTrue(Label + FString::Printf(TEXT("manifestPath is %s (read %s)"), *ManifestPath, *Read.ManifestPath),
			Read.ManifestPath.Equals(ManifestPath, ESearchCase::CaseSensitive));
	}
	if (T.TestTrue(Label + TEXT("the case states `level`"), ReadNullableString(Result, TEXT("level"), Level)))
	{
		T.TestTrue(Label + FString::Printf(TEXT("level is %s (read %s)"), Level.IsEmpty() ? TEXT("null") : *Level,
			Read.Level.IsEmpty() ? TEXT("null") : *Read.Level), Read.Level.Equals(Level, ESearchCase::CaseSensitive));
	}
	if (T.TestTrue(Label + TEXT("the case states `refusal`"), ReadNullableString(Result, TEXT("refusal"), Refusal)))
	{
		T.TestEqual(Label + TEXT("refusal"), Read.Refusal, Refusal);
	}
	if (bRefused)
	{
		T.TestTrue(Label + TEXT("a refusal carries a message"), !Read.Error.IsEmpty());
	}

	// `frames`: stated for a 3.x read and only then, equal to what the reader put on the read, every
	// member exactly (a geoid the manifest names none for is `null`).
	const TSharedPtr<FJsonObject>* ExpectedFrames = nullptr;
	if (Result->TryGetObjectField(TEXT("frames"), ExpectedFrames) && ExpectedFrames != nullptr)
	{
		if (T.TestTrue(Label + TEXT("the reader read the frames"), Read.Frames.IsValid()))
		{
			T.TestTrue(Label + TEXT("the frames are the case's"),
				FJsonValue::CompareEqual(FJsonValueObject(*ExpectedFrames), FJsonValueObject(Read.Frames)));
		}
	}
	else
	{
		T.TestFalse(Label + TEXT("the reader read frames the case does not state"), Read.Frames.IsValid());
	}

	// `files`, as a set keyed by path, every member exactly.
	TMap<FString, FString> ExpectedFiles;
	const TArray<TSharedPtr<FJsonValue>>* Files = nullptr;
	if (T.TestTrue(Label + TEXT("the case states `files`"), Result->TryGetArrayField(TEXT("files"), Files)))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Files)
		{
			const TSharedPtr<FJsonObject>* Row = nullptr;
			FString Path, Family, Frame;
			if (!Value->TryGetObject(Row) || Row == nullptr || !(*Row)->TryGetStringField(TEXT("path"), Path)
				|| !ReadNullableString(*Row, TEXT("family"), Family) || !ReadNullableString(*Row, TEXT("frame"), Frame))
			{
				T.AddError(Label + TEXT("a `files` row is not {path, family, frame, count}"));
				continue;
			}
			const TSharedPtr<FJsonValue> CountValue = (*Row)->TryGetField(TEXT("count"));
			const FString Count = CountValue.IsValid() && CountValue->Type == EJson::Number
				? FString::Printf(TEXT("%lld"), static_cast<int64>(CountValue->AsNumber()))
				: TEXT("null");
			ExpectedFiles.Add(Path, DescribeFile(Family, Frame, Count));
		}
	}
	TMap<FString, FString> FoundFiles;
	for (const FMantlePlaceBundleFile& File : Read.Files)
	{
		FoundFiles.Add(File.Path, DescribeFile(FMantlePlaceBundleReaderLogic::FamilyName(File.Family),
			FMantlePlaceBundleReaderLogic::FrameName(File.Frame),
			File.bHasCount ? FString::Printf(TEXT("%lld"), File.Count) : FString(TEXT("null"))));
		T.TestEqual(Label + File.Path + TEXT(": the frame is the family's"),
			File.Frame, FMantlePlaceBundleReaderLogic::FrameOf(File.Family));
		T.TestEqual(Label + File.Path + TEXT(": the unit is the family's"),
			File.Unit, FMantlePlaceBundleReaderLogic::UnitOf(File.Family));
	}
	for (const TPair<FString, FString>& Pair : ExpectedFiles)
	{
		const FString* Found = FoundFiles.Find(Pair.Key);
		if (Found == nullptr)
		{
			T.AddError(Label + TEXT("did not find ") + Pair.Key);
		}
		else
		{
			T.TestEqual(Label + Pair.Key, *Found, Pair.Value);
		}
	}
	for (const TPair<FString, FString>& Pair : FoundFiles)
	{
		if (!ExpectedFiles.Contains(Pair.Key))
		{
			T.AddError(Label + TEXT("found a file the case does not: ") + Pair.Key);
		}
	}

	// `skipped`, the same way.
	TMap<FString, FString> ExpectedSkips;
	const TArray<TSharedPtr<FJsonValue>>* Skipped = nullptr;
	if (T.TestTrue(Label + TEXT("the case states `skipped`"), Result->TryGetArrayField(TEXT("skipped"), Skipped)))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Skipped)
		{
			const TSharedPtr<FJsonObject>* Row = nullptr;
			FString Path, Why, Reason;
			if (!Value->TryGetObject(Row) || Row == nullptr || !(*Row)->TryGetStringField(TEXT("path"), Path)
				|| !(*Row)->TryGetStringField(TEXT("why"), Why) || !ReadNullableString(*Row, TEXT("reason"), Reason))
			{
				T.AddError(Label + TEXT("a `skipped` row is not {path, why, reason}"));
				continue;
			}
			ExpectedSkips.Add(Path, Why + TEXT(" / ") + (Reason.IsEmpty() ? TEXT("null") : Reason));
		}
	}
	TMap<FString, FString> FoundSkips;
	for (const FMantlePlaceBundleSkipped& Skip : Read.Skipped)
	{
		FoundSkips.Add(Skip.Path, FString(FMantlePlaceBundleReaderLogic::SkipName(Skip.Why)) + TEXT(" / ")
			+ (Skip.Reason.IsEmpty() ? TEXT("null") : Skip.Reason));
	}
	for (const TPair<FString, FString>& Pair : ExpectedSkips)
	{
		const FString* Found = FoundSkips.Find(Pair.Key);
		if (Found == nullptr)
		{
			T.AddError(Label + TEXT("did not skip ") + Pair.Key);
		}
		else
		{
			T.TestTrue(Label + Pair.Key + TEXT(" is skipped as ") + Pair.Value + TEXT(" (read ") + *Found + TEXT(")"),
				Found->Equals(Pair.Value, ESearchCase::CaseSensitive));
		}
	}
	for (const TPair<FString, FString>& Pair : FoundSkips)
	{
		if (!ExpectedSkips.Contains(Pair.Key))
		{
			T.AddError(Label + TEXT("skipped a file the case does not: ") + Pair.Key);
		}
	}
}

/** The two frames a readable 3.x manifest states (format.md §9.4): a metric order and the local site frame.
 *  A 3.x manifest without them is refused as unreadable, as the .NET reader refuses it. */
const TCHAR* const MetricFrames = TEXT(
	"\"frames\": {\"georeferenced\": {\"horizontal_crs\": {\"code\": \"EPSG:32618\"},"
	" \"vertical_crs\": {\"code\": \"EPSG:3855\"}, \"geoid_model\": \"EGM2008\","
	" \"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": 1}, \"vertical_unit\": {\"name\": \"metre\", \"to_metres\": 1},"
	" \"axis_order\": \"east-north\", \"grid_rotation_deg\": 0.67},"
	" \"local\": {\"origin\": {\"longitude\": -73.975, \"latitude\": 40.755, \"ellipsoidal_height_m\": -12.5,"
	" \"height_m\": 20.0, \"projected\": {\"crs\": {\"code\": \"EPSG:32618\"}, \"x\": 586500.0, \"y\": 4512000.0,"
	" \"unit\": {\"name\": \"metre\", \"to_metres\": 1}}},"
	" \"vertical_crs\": {\"code\": \"EPSG:3855\"}, \"geoid_model\": \"EGM2008\","
	" \"unit\": {\"name\": \"metre\", \"to_metres\": 1}, \"axes\": {\"x\": \"east\", \"y\": \"north\", \"z\": \"up\"},"
	" \"grid_rotation_deg\": 0.67}}");
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceReaderCorpusTest,
	"MantlePlace.Import.ReaderCorpus",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceReaderCorpusTest::RunTest(const FString& Parameters)
{
	if (SkipWithoutCorpus(*this))
	{
		return true;
	}

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("reader"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	const FString CorpusRoot = FindCorpusRoot();
	const FString Scratch = FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("MantlePlace"), TEXT("Tests"), TEXT("ReaderCorpus-") + FGuid::NewGuid().ToString());
	ON_SCOPE_EXIT { IFileManager::Get().DeleteDirectory(*Scratch, /*RequireExists*/ false, /*Tree*/ true); };

	int32 Ran = 0;
	for (const FCase& Case : Cases)
	{
		FString BundleFile;
		const TSharedPtr<FJsonObject>* ResultPtr = nullptr;
		if (!Case.PayloadObject.IsValid() || !Case.PayloadObject->TryGetStringField(TEXT("bundle"), BundleFile)
			|| !Case.PayloadObject->TryGetObjectField(TEXT("result"), ResultPtr) || ResultPtr == nullptr)
		{
			AddError(Case.What(TEXT("is not {bundle, result}; a case the suite cannot run fails it")));
			continue;
		}
		TSharedPtr<FJsonObject> Fixture;
		FString FixtureText;
		const TSharedPtr<FJsonObject>* FilesPtr = nullptr;
		if (!Detail::LoadJsonObject(FPaths::Combine(CorpusRoot, BundleFile), Fixture, FixtureText)
			|| !Fixture->TryGetObjectField(TEXT("files"), FilesPtr) || FilesPtr == nullptr)
		{
			AddError(Case.What(*FString::Printf(TEXT("names the bundle %s, which has no `files` to build"), *BundleFile)));
			continue;
		}
		TMap<FString, TArray<uint8>> Files;
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*FilesPtr)->Values)
		{
			Files.Add(Pair.Key, BytesOfValue(Pair.Value));
		}

		TArray<EForm> Forms = { EForm::Zip, EForm::Wrapped };
		if (!HasCaseCollision(Files))
		{
			Forms.Insert(EForm::Folder, 0);
		}
		for (const EForm Form : Forms)
		{
			const FString Label = FString::Printf(TEXT("[%s, %s] "), *Case.Id, FormName(Form));
			const FString Dir = FPaths::Combine(Scratch, FGuid::NewGuid().ToString());
			const FString Path = Build(Form, Files, Dir);
			if (!TestFalse(Label + TEXT("the bundle is built"), Path.IsEmpty()))
			{
				continue;
			}
			const FMantlePlaceBundleRead Read = MantlePlaceBundleArchive::Read(Path);
			CompareResult(*this, Label, *ResultPtr, Read);
			if (Case.IsReject() && !Case.ErrorContains.IsEmpty())
			{
				TestTrue(Label + FString::Printf(TEXT("the refusal names %s: %s"), *Case.ErrorContains, *Read.Error),
					Read.Error.Contains(Case.ErrorContains, ESearchCase::CaseSensitive));
			}
			TestEqual(Label + TEXT("the wrapper folder is found"), Read.Root,
				Form == EForm::Wrapped ? FString(TEXT("site-bundle/")) : FString());
		}
		++Ran;
	}
	TestEqual(TEXT("every case of the reader group ran"), Ran, Cases.Num());
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceBundleReaderLogicTest,
	"MantlePlace.Import.BundleReaderLogic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceBundleReaderLogicTest::RunTest(const FString& Parameters)
{
	using L = FMantlePlaceBundleReaderLogic;

	// --- The family table: by extension in 3.x, by the file's own frame statement in 2.x --------------
	TestEqual(TEXT("a GeoTIFF is georeferenced"), L::FamilyOfExtension(TEXT("Elevation/Elevation.tif")), EMantlePlaceBundleFamily::Georeferenced);
	TestEqual(TEXT("a LandXML is georeferenced"), L::FamilyOfExtension(TEXT("Elevation/Terrain.landxml")), EMantlePlaceBundleFamily::Georeferenced);
	TestEqual(TEXT("an extension is matched regardless of case"), L::FamilyOfExtension(TEXT("Basemap/Roads.GPKG")), EMantlePlaceBundleFamily::Georeferenced);
	TestEqual(TEXT("a glTF is host-ready"), L::FamilyOfExtension(TEXT("Elevation/Terrain.glb")), EMantlePlaceBundleFamily::HostReady);
	TestEqual(TEXT("a CSV is host-ready"), L::FamilyOfExtension(TEXT("Landcover/Trees.csv")), EMantlePlaceBundleFamily::HostReady);
	TestEqual(TEXT("the OBJ's material file is in neither"), L::FamilyOfExtension(TEXT("Elevation/Terrain.mtl")), EMantlePlaceBundleFamily::None);
	TestEqual(TEXT("the manifest is in neither"), L::FamilyOfExtension(TEXT("Manifest.json")), EMantlePlaceBundleFamily::None);
	TestEqual(TEXT("a folder's dot is not an extension"), L::FamilyOfExtension(TEXT("a.tif/Readme")), EMantlePlaceBundleFamily::None);
	TestEqual(TEXT("2.x absolute is georeferenced"), L::FamilyOfCoordinateMode(TEXT("absolute")), EMantlePlaceBundleFamily::Georeferenced);
	TestEqual(TEXT("2.x offset is host-ready"), L::FamilyOfCoordinateMode(TEXT("offset")), EMantlePlaceBundleFamily::HostReady);
	TestEqual(TEXT("an unknown 2.x mode is in neither"), L::FamilyOfCoordinateMode(TEXT("geographic")), EMantlePlaceBundleFamily::None);
	TestEqual(TEXT("georeferenced is in the order frame"), L::FrameOf(EMantlePlaceBundleFamily::Georeferenced), EMantlePlaceBundleFrame::Order);
	TestEqual(TEXT("host-ready is in the site frame"), L::FrameOf(EMantlePlaceBundleFamily::HostReady), EMantlePlaceBundleFrame::Site);
	TestEqual(TEXT("georeferenced is in the order's unit"), L::UnitOf(EMantlePlaceBundleFamily::Georeferenced), EMantlePlaceBundleUnit::OrderDelivery);
	TestEqual(TEXT("host-ready is in metres"), L::UnitOf(EMantlePlaceBundleFamily::HostReady), EMantlePlaceBundleUnit::Metres);

	// --- Where the manifest is -----------------------------------------------------------------------
	{
		FString Root, Manifest;
		TestTrue(TEXT("a root Manifest.json is found"), L::FindManifest({ TEXT("Manifest.json"), TEXT("Readme.md") }, Root, Manifest));
		TestEqual(TEXT("... at the root"), Root, FString());
		TestEqual(TEXT("... as the 3.x manifest"), Manifest, FString(L::ManifestPath3()));

		TestTrue(TEXT("a 2.x manifest is found"), L::FindManifest({ TEXT("Metadata/manifest.json") }, Root, Manifest));
		TestEqual(TEXT("... as the 2.x manifest"), Manifest, FString(L::ManifestPath2()));

		TestTrue(TEXT("a manifest in one wrapper folder is found"),
			L::FindManifest({ TEXT("Site/Manifest.json"), TEXT("Site/Elevation/Elevation.tif") }, Root, Manifest));
		TestEqual(TEXT("... with the wrapper as the root"), Root, FString(TEXT("Site/")));

		TestTrue(TEXT("the manifest's name is matched regardless of case"), L::FindManifest({ TEXT("MANIFEST.JSON") }, Root, Manifest));
		TestTrue(TEXT("macOS metadata beside the wrapper does not hide it"),
			L::FindManifest({ TEXT("Site/Manifest.json"), TEXT("__MACOSX/Site/._Manifest.json") }, Root, Manifest));

		TestFalse(TEXT("two top-level folders are no wrapper"),
			L::FindManifest({ TEXT("A/Manifest.json"), TEXT("B/Readme.md") }, Root, Manifest));
		TestFalse(TEXT("a manifest two folders down is not a bundle's"),
			L::FindManifest({ TEXT("A/B/Manifest.json") }, Root, Manifest));
		TestFalse(TEXT("a zip with neither manifest is not a bundle"), L::FindManifest({ TEXT("Readme.md") }, Root, Manifest));
	}
	TestFalse(TEXT("an entry that climbs out is ignored"), L::IsReadableEntry(TEXT("../Manifest.json")));
	TestFalse(TEXT("an absolute entry is ignored"), L::IsReadableEntry(TEXT("/Manifest.json")));
	TestFalse(TEXT("a folder entry is ignored"), L::IsReadableEntry(TEXT("Elevation/")));
	TestTrue(TEXT("a plain path is read"), L::IsReadableEntry(TEXT("Elevation/Elevation.tif")));

	// --- The major gate --------------------------------------------------------------------------------
	{
		FInMemoryBundle Ahead;
		Ahead.Entries.Add(TEXT("Manifest.json"), TEXT("{\"version\": \"4.0.0\", \"catalog\": {\"Readme.md\": {}}}"));
		Ahead.Entries.Add(TEXT("Readme.md"), TEXT("text"));
		const FMantlePlaceBundleRead Read = Ahead.Read();
		TestEqual(TEXT("a major above 3 is refused"), Read.Outcome, EMantlePlaceBundleReadOutcome::Refused);
		TestEqual(TEXT("... as major-ahead"), Read.Refusal, FString(L::MajorAheadRefusal()));
		TestEqual(TEXT("... with the plugin's update message"), Read.Error, L::NewerMajorMessage(TEXT("4.0.0"), 3));
		TestTrue(TEXT("... which names the version"), Read.Error.Contains(TEXT("4.0.0")));
		TestEqual(TEXT("... and reads no catalog"), Read.Files.Num() + Read.Skipped.Num(), 0);
	}
	{
		FInMemoryBundle Three;
		Three.Entries.Add(TEXT("Manifest.json"), FString(TEXT("{\"version\": \"3.4.0\", \"level\": \"Med\", ")) + MetricFrames
			+ TEXT(", \"catalog\": {\"Manifest.json\": {}}}"));
		TestEqual(TEXT("any 3.x minor is read"), Three.Read().Outcome, EMantlePlaceBundleReadOutcome::Read);

		// A 3.x manifest that states no frames is refused, naming what is missing, and reads no catalog.
		FInMemoryBundle Bare;
		Bare.Entries.Add(TEXT("Manifest.json"), TEXT("{\"version\": \"3.0.0\", \"level\": \"Med\", \"catalog\": {\"Manifest.json\": {}}}"));
		const FMantlePlaceBundleRead BareRead = Bare.Read();
		TestEqual(TEXT("a 3.x manifest with no frames is refused"), BareRead.Outcome, EMantlePlaceBundleReadOutcome::Refused);
		TestEqual(TEXT("... as unreadable"), BareRead.Refusal, FString(L::UnreadableRefusal()));
		TestTrue(TEXT("... naming the frames"), BareRead.Error.Contains(TEXT("frames")));
		TestEqual(TEXT("... with no level and no catalog read"), BareRead.Level.Len() + BareRead.Files.Num() + BareRead.Skipped.Num(), 0);
	}
	{
		// The 2.x reader keeps its own message, word for word: the shared text is one function now.
		FString Error;
		MantlePlaceImportManifest::Parse(TEXT("{\"version\": \"3.0.0\"}"), Error);
		TestEqual(TEXT("the 2.x parse refuses a 3.0 manifest with the same update message, as it shipped"),
			Error, L::NewerMajorMessage(TEXT("3.0.0"), MantlePlaceMaxSupportedManifestMajor));
		TestTrue(TEXT("... which still tells the user to update the plugin"),
			Error.Contains(TEXT("Update the Mantle Place plugin to import this bundle.")));
	}
	{
		FInMemoryBundle Misplaced;
		Misplaced.Entries.Add(TEXT("Manifest.json"), TEXT("{\"version\": \"2.0.0\", \"file_entries\": {}}"));
		TestEqual(TEXT("a root manifest of a 2.x version is not read"), Misplaced.Read().Outcome, EMantlePlaceBundleReadOutcome::Failed);
	}
	{
		FInMemoryBundle Old;
		Old.Entries.Add(TEXT("Metadata/manifest.json"), TEXT("{\"version\": 19}"));
		const FMantlePlaceBundleRead Read = Old.Read();
		TestEqual(TEXT("an integer-era version is below the floor"), Read.Outcome, EMantlePlaceBundleReadOutcome::Failed);
		TestEqual(TEXT("... with the floor's message"), Read.Error, L::BelowFloorMessage(TEXT(""), MantlePlaceMinSupportedManifestVersion));
	}

	// --- Nothing from a per-host block ---------------------------------------------------------------
	{
		// A 3.x manifest that carries a host block anyway: the file it names is not in the catalog, so it
		// is not found, and the block's catalog-looking member is never read as one.
		FInMemoryBundle Hosted;
		Hosted.Entries.Add(TEXT("Manifest.json"), FString(TEXT("{\"version\": \"3.0.0\", \"level\": \"Max\", ")) + MetricFrames + TEXT(","
			" \"catalog\": {\"Elevation/Elevation.tif\": {\"count\": {\"kind\": \"pixels\", \"value\": 4}}},"
			" \"hosts\": {\"unreal\": {\"heightmap\": \"Elevation/Elevation.png\","
			"   \"file_entries\": {\"Elevation/Elevation.png\": {}}}}}"));
		Hosted.Entries.Add(TEXT("Elevation/Elevation.tif"), TEXT("tif"));
		Hosted.Entries.Add(TEXT("Elevation/Elevation.png"), TEXT("png"));
		const FMantlePlaceBundleRead Read = Hosted.Read();
		TestEqual(TEXT("a 3.x bundle with a host block is read"), Read.Outcome, EMantlePlaceBundleReadOutcome::Read);
		TestEqual(TEXT("... finding only what the catalog names"), Read.Files.Num(), 1);
		TestNull(TEXT("... and not the file the host block points at"), FindFile(Read, TEXT("Elevation/Elevation.png")));
		if (const FMantlePlaceBundleFile* Tif = FindFile(Read, TEXT("Elevation/Elevation.tif")))
		{
			TestTrue(TEXT("... with the count the catalog states"), Tif->bHasCount && Tif->Count == 4);
		}
	}

	// --- What a catalog entry states, and what it does not ------------------------------------------
	{
		FInMemoryBundle Counts;
		Counts.Entries.Add(TEXT("Manifest.json"), FString(TEXT("{\"version\": \"3.0.0\", \"level\": \"Min\", ")) + MetricFrames + TEXT(
			", \"catalog\": {"
			" \"A.csv\": {\"count\": {\"kind\": \"rows\", \"value\": 12}}, \"B.csv\": {\"count\": {\"kind\": \"rows\", \"value\": 1.5}},"
			" \"C.csv\": {\"count\": {\"kind\": \"rows\", \"value\": -1}}, \"D.csv\": {\"count\": {\"kind\": \"rows\", \"value\": true}},"
			" \"E.csv\": {\"count\": {\"kind\": \"rows\", \"value\": \"7\"}}, \"G.csv\": {\"count\": 5}, \"F.csv\": {}},"
			" \"not_held\": [{\"path\": \"Records/Parcels.gpkg\", \"reason\": \"pending\"}]}"));
		for (const TCHAR* Name : { TEXT("A.csv"), TEXT("b.CSV"), TEXT("C.csv"), TEXT("D.csv"), TEXT("E.csv"), TEXT("G.csv") })
		{
			Counts.Entries.Add(Name, TEXT("x"));
		}
		const FMantlePlaceBundleRead Read = Counts.Read();
		TestEqual(TEXT("the level is the manifest's"), Read.Level, FString(TEXT("Min")));
		const FMantlePlaceBundleFile* A = FindFile(Read, TEXT("A.csv"));
		TestTrue(TEXT("a whole count is read"), A != nullptr && A->bHasCount && A->Count == 12);
		for (const TCHAR* Name : { TEXT("B.csv"), TEXT("C.csv"), TEXT("D.csv"), TEXT("E.csv"), TEXT("G.csv") })
		{
			const FMantlePlaceBundleFile* File = FindFile(Read, Name);
			TestTrue(FString::Printf(TEXT("%s: a count that is not a non-negative whole number is none"), Name),
				File != nullptr && !File->bHasCount);
		}
		const FMantlePlaceBundleFile* B = FindFile(Read, TEXT("B.csv"));
		TestTrue(TEXT("a catalog name finds its file regardless of case, at the archive's own spelling"),
			B != nullptr && B->EntryName == TEXT("b.CSV"));
		TestEqual(TEXT("a catalog name with no file and a not-held file are both skipped"), Read.Skipped.Num(), 2);
		if (Read.Skipped.Num() == 2)
		{
			TestEqual(TEXT("... sorted by path"), Read.Skipped[0].Path, FString(TEXT("F.csv")));
			TestEqual(TEXT("... F.csv as missing"), Read.Skipped[0].Why, EMantlePlaceBundleSkip::FileMissing);
			TestEqual(TEXT("... the not-held file with its reason verbatim"), Read.Skipped[1].Reason, FString(TEXT("pending")));
		}
	}
	return true;
}

// --- The frames, value by value ----------------------------------------------------------------------
// The corpus's `reader.v3Frames*` cases pin one refusal each. This pins each rule on its own, against
// the metric frames above with one value changed: a unit is its factor and never its name, an axis
// order, a CRS that names no form, the local axes and the local unit, and that a read hands the
// manifest's frames back as stated, never converted.

namespace
{
/** A 3.x manifest stating `MetricFrames` with one stated fragment replaced by another. */
FMantlePlaceBundleRead ReadWithFrames(const TCHAR* From, const TCHAR* To)
{
	FString Frames = MetricFrames;
	if (From != nullptr)
	{
		const int32 Found = Frames.Find(From, ESearchCase::CaseSensitive);
		check(Found != INDEX_NONE); // the test's own fragment must exist, or it proves nothing
		Frames = Frames.Left(Found) + To + Frames.Mid(Found + FCString::Strlen(From));
	}
	FInMemoryBundle Bundle;
	Bundle.Entries.Add(TEXT("Manifest.json"),
		FString(TEXT("{\"version\": \"3.0.0\", \"level\": \"Med\", ")) + Frames + TEXT(", \"catalog\": {\"Manifest.json\": {}}}"));
	return Bundle.Read();
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceBundleReaderFramesTest,
	"MantlePlace.Import.BundleReaderFrames",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceBundleReaderFramesTest::RunTest(const FString& Parameters)
{
	using L = FMantlePlaceBundleReaderLogic;

	// A refusal: the manifest is refused as unreadable, the message names the value, no level is read.
	const auto ExpectRefused = [this](const FString& What, const FMantlePlaceBundleRead& Read, std::initializer_list<const TCHAR*> Named)
	{
		TestEqual(What + TEXT(": refused"), Read.Outcome, EMantlePlaceBundleReadOutcome::Refused);
		TestEqual(What + TEXT(": as unreadable"), Read.Refusal, FString(L::UnreadableRefusal()));
		TestEqual(What + TEXT(": with no level read"), Read.Level.Len(), 0);
		for (const TCHAR* Value : Named)
		{
			TestTrue(What + FString::Printf(TEXT(": the message names %s: %s"), Value, *Read.Error),
				Read.Error.Contains(Value, ESearchCase::CaseSensitive));
		}
	};
	const auto ExpectRead = [this](const FString& What, const FMantlePlaceBundleRead& Read)
	{
		TestEqual(What + TEXT(": read"), Read.Outcome, EMantlePlaceBundleReadOutcome::Read);
		TestTrue(What + TEXT(": frames handed back"), Read.Frames.IsValid());
	};

	ExpectRead(TEXT("the metric frames"), ReadWithFrames(nullptr, nullptr));

	// A unit is its factor, never its name.
	const TCHAR* const OrderUnit = TEXT("\"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": 1}");
	ExpectRefused(TEXT("a metre named so, at twice the factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": 2}")), { TEXT("metre"), TEXT("2") });
	ExpectRefused(TEXT("a unit of an unknown name and factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"furlong\", \"to_metres\": 201.168}")),
		{ TEXT("furlong"), TEXT("201.168") });
	ExpectRefused(TEXT("a foot named so at the wrong factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"foot\", \"to_metres\": 0.3}")), { TEXT("foot"), TEXT("0.3") });
	ExpectRefused(TEXT("a zero factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": 0}")), { TEXT("horizontal_unit") });
	ExpectRefused(TEXT("a negative factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": -1}")), { TEXT("horizontal_unit") });
	ExpectRefused(TEXT("a unit with no factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"foot\"}")), { TEXT("horizontal_unit"), TEXT("foot") });
	ExpectRead(TEXT("the international foot by its factor, whatever it is called"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"metre\", \"to_metres\": 0.3048}")));
	ExpectRead(TEXT("the US survey foot by its factor"),
		ReadWithFrames(OrderUnit, TEXT("\"horizontal_unit\": {\"name\": \"US survey foot\", \"to_metres\": 0.3048006096012192}")));

	// An axis order that is neither east-north nor north-east.
	const TCHAR* const AxisOrder = TEXT("\"axis_order\": \"east-north\"");
	ExpectRead(TEXT("north-east"), ReadWithFrames(AxisOrder, TEXT("\"axis_order\": \"north-east\"")));
	ExpectRefused(TEXT("an axis order of another word"),
		ReadWithFrames(AxisOrder, TEXT("\"axis_order\": \"NE\"")), { TEXT("axis_order"), TEXT("NE") });
	ExpectRefused(TEXT("an axis order with the up axis"),
		ReadWithFrames(AxisOrder, TEXT("\"axis_order\": \"east-north-up\"")), { TEXT("east-north-up") });
	ExpectRefused(TEXT("an axis order that is not a string"),
		ReadWithFrames(AxisOrder, TEXT("\"axis_order\": 1")), { TEXT("axis_order") });

	// A CRS that names no code, PROJJSON object or WKT2 string.
	const TCHAR* const OrderCrs = TEXT("\"horizontal_crs\": {\"code\": \"EPSG:32618\"}");
	ExpectRead(TEXT("a PROJJSON object CRS"),
		ReadWithFrames(OrderCrs, TEXT("\"horizontal_crs\": {\"projjson\": {\"type\": \"ProjectedCRS\"}}")));
	ExpectRead(TEXT("a WKT2 string CRS"),
		ReadWithFrames(OrderCrs, TEXT("\"horizontal_crs\": {\"wkt2\": \"PROJCRS[\\\"Synthetic\\\"]\"}")));
	ExpectRefused(TEXT("a CRS naming nothing"), ReadWithFrames(OrderCrs, TEXT("\"horizontal_crs\": {}")), { TEXT("horizontal_crs") });
	ExpectRefused(TEXT("a CRS that is a bare number"),
		ReadWithFrames(OrderCrs, TEXT("\"horizontal_crs\": {\"code\": \"32618\"}")), { TEXT("horizontal_crs"), TEXT("32618") });
	ExpectRefused(TEXT("a vertical datum that names nothing"),
		ReadWithFrames(TEXT("\"vertical_crs\": {\"code\": \"EPSG:3855\"}, \"geoid_model\": \"EGM2008\", \"horizontal_unit\""),
			TEXT("\"vertical_crs\": {\"name\": \"local datum\"}, \"geoid_model\": \"EGM2008\", \"horizontal_unit\"")),
		{ TEXT("frames.georeferenced.vertical_crs") });

	// The local frame runs east, north, up, in metres.
	const TCHAR* const LocalAxes = TEXT("\"axes\": {\"x\": \"east\", \"y\": \"north\", \"z\": \"up\"}");
	ExpectRefused(TEXT("a local frame whose up is down"),
		ReadWithFrames(LocalAxes, TEXT("\"axes\": {\"x\": \"east\", \"y\": \"north\", \"z\": \"down\"}")), { TEXT("down") });
	ExpectRefused(TEXT("a local frame with its x and y swapped"),
		ReadWithFrames(LocalAxes, TEXT("\"axes\": {\"x\": \"north\", \"y\": \"east\", \"z\": \"up\"}")), { TEXT("axes"), TEXT("north") });
	ExpectRefused(TEXT("a local frame stating no axes"), ReadWithFrames(LocalAxes, TEXT("\"axes\": {}")), { TEXT("axes") });
	const TCHAR* const LocalUnit = TEXT("\"unit\": {\"name\": \"metre\", \"to_metres\": 1}, \"axes\"");
	ExpectRefused(TEXT("a local unit that is a foot"),
		ReadWithFrames(LocalUnit, TEXT("\"unit\": {\"name\": \"foot\", \"to_metres\": 0.3048}, \"axes\"")), { TEXT("foot"), TEXT("0.3048") });
	ExpectRefused(TEXT("a local unit that is the US survey foot"),
		ReadWithFrames(LocalUnit, TEXT("\"unit\": {\"name\": \"US survey foot\", \"to_metres\": 0.3048006096012192}, \"axes\"")),
		{ TEXT("US survey foot") });
	ExpectRefused(TEXT("a local unit that is unknown"),
		ReadWithFrames(LocalUnit, TEXT("\"unit\": {\"name\": \"cubit\", \"to_metres\": 0.4572}, \"axes\"")), { TEXT("cubit") });

	// The frames come back as the manifest states them: nothing converted, derived or renamed.
	{
		const FMantlePlaceBundleRead Feet = ReadWithFrames(OrderUnit,
			TEXT("\"horizontal_unit\": {\"name\": \"US survey foot\", \"to_metres\": 0.3048006096012192}"));
		ExpectRead(TEXT("an order in US survey feet"), Feet);
		if (Feet.Frames.IsValid())
		{
			const TSharedPtr<FJsonObject>* Georeferenced = nullptr;
			const TSharedPtr<FJsonObject>* Unit = nullptr;
			if (TestTrue(TEXT("verbatim: the georeferenced frame is there"), Feet.Frames->TryGetObjectField(TEXT("georeferenced"), Georeferenced))
				&& TestTrue(TEXT("verbatim: its horizontal unit is there"), (*Georeferenced)->TryGetObjectField(TEXT("horizontal_unit"), Unit)))
			{
				TestEqual(TEXT("verbatim: the unit's name is the manifest's"), (*Unit)->GetStringField(TEXT("name")), FString(TEXT("US survey foot")));
				TestEqual(TEXT("verbatim: the unit's factor is the manifest's, not 1 and not 0.3048"),
					(*Unit)->GetNumberField(TEXT("to_metres")), 0.3048006096012192);
				TestEqual(TEXT("verbatim: the axis order is the manifest's"),
					(*Georeferenced)->GetStringField(TEXT("axis_order")), FString(TEXT("east-north")));
				TestEqual(TEXT("verbatim: the grid rotation is the manifest's"),
					(*Georeferenced)->GetNumberField(TEXT("grid_rotation_deg")), 0.67);
			}
		}
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
