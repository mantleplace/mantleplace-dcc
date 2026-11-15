// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"

class FJsonObject;

/** The family a bundle file belongs to (spec/format.md §4), or neither (`Readme.md`, `Terrain.mtl`). */
enum class EMantlePlaceBundleFamily : uint8
{
	None,
	Georeferenced,
	HostReady,
};

/** The frame a file's coordinates are in. Always its family's: there is no third frame and no file in two. */
enum class EMantlePlaceBundleFrame : uint8
{
	None,
	Order, // absolute, in the order's projected CRS and vertical datum
	Site,  // offsets from the site origin, in the local site frame
};

/** The unit a file's coordinates are in, by family: the order's delivery unit, or metres on every order. */
enum class EMantlePlaceBundleUnit : uint8
{
	None,
	OrderDelivery, // the georeferenced frame's unit, stated once in the manifest
	Metres,        // the local site frame's unit, on every order
};

/** One file the reader found: a name the catalog promises that the bundle holds. */
struct FMantlePlaceBundleFile
{
	FString Path;      // the catalog's key, as written
	FString EntryName; // the archive entry it is at: the wrapper folder, if any, and the archive's own spelling
	EMantlePlaceBundleFamily Family = EMantlePlaceBundleFamily::None;
	EMantlePlaceBundleFrame Frame = EMantlePlaceBundleFrame::None;
	EMantlePlaceBundleUnit Unit = EMantlePlaceBundleUnit::None;
	bool bHasCount = false; // false when the catalog states no count (every 2.x bundle)
	int64 Count = 0;        // the count the catalog STATES: rows, features, triangles or pixels
};

/** Why a file the manifest names is not read. */
enum class EMantlePlaceBundleSkip : uint8
{
	FileMissing, // the catalog names it and the download holds no such file: reported, never fatal
	NotHeld,     // the manifest lists it among the promised files this download does not hold
};

struct FMantlePlaceBundleSkipped
{
	FString Path;
	EMantlePlaceBundleSkip Why = EMantlePlaceBundleSkip::FileMissing;
	FString Reason; // the manifest's stated reason, verbatim, for NotHeld; empty for FileMissing
};

enum class EMantlePlaceBundleReadOutcome : uint8
{
	Read,    // the manifest was read and the catalog walked
	Refused, // a major above any this reader speaks, or 3.x frames it cannot apply: stopped, nothing extracted
	Failed,  // not a bundle this reader can read at all: no manifest, not JSON, no version, below the floor
};

/**
 * What the reader found in one bundle, in the shape the conformance corpus's `reader` group states
 * (tools/manifest-conformance/corpus/README.md, "The reader group"). The members it reads before it
 * refuses (Version, Major, ManifestPath) are set on a refusal too (HPS-37).
 */
struct FMantlePlaceBundleRead
{
	EMantlePlaceBundleReadOutcome Outcome = EMantlePlaceBundleReadOutcome::Failed;
	FString Refusal;      // "major-ahead" or "unreadable" on a refusal, else empty
	FString Error;        // the user-facing message on a refusal or a failure, else empty
	FString Version;      // the manifest's version string, verbatim
	int32 Major = 0;      // its parsed major; 0 when it did not parse
	FString ManifestPath; // where the manifest is, from the bundle's root: Manifest.json or Metadata/manifest.json
	FString Root;         // the one wrapper folder the bundle sits in ("Bundle/"), or empty
	FString Level;        // the download's level as the manifest states it once (Max, Med, Min); empty for 2.x
	/** A 3.x read: the manifest's `frames` member as stated, with each `geoid_model` stated (null when the
	 *  manifest names none). Nothing in it is derived. Null for 2.x and for a refusal. */
	TSharedPtr<FJsonObject> Frames;
	TArray<FMantlePlaceBundleFile> Files;      // sorted by Path
	TArray<FMantlePlaceBundleSkipped> Skipped; // sorted by Path

	bool IsRead() const { return Outcome == EMantlePlaceBundleReadOutcome::Read; }
};

/**
 * Pure logic for reading a bundle of either layout: where its manifest is, which major it is, and
 * what its one catalog promises, found and not found. Nothing here opens a file:
 * MantlePlaceBundleArchive lists a folder or a zip and hands the entry names and a text reader in, so
 * the corpus drives this unit headless (`MantlePlace.Import.ReaderCorpus`).
 *
 *  - **The manifest is found by where it is.** `Manifest.json` at the root is 3.x or later;
 *    `Metadata/manifest.json` is 2.x or 1.x. Either may sit inside one wrapper folder. The name is
 *    matched regardless of case, as every archive path here is (unreal/CLAUDE.md).
 *  - **The version gates before anything else is read.** A major above NewestMajor is refused with
 *    the plugin's update message, naming the version, and no catalog is read (format.md §14).
 *  - **Every file is found by its name in the one catalog** (`file_entries`). Nothing is read from a
 *    per-host block: a 3.x manifest has none (format.md §9.7), and this unit reads no `hosts` member
 *    of either major. A catalog name the bundle does not hold is skipped as missing, never fatal; a
 *    file the catalog does not name is neither found nor skipped.
 *  - **Family, frame and unit come from the extension in 3.x** (format.md §4) and from the file's own
 *    frame statement in 2.x. The count and the level are what the manifest states; the reader never
 *    counts a file or takes a level from a name.
 *
 * The 2.x import path is MantlePlaceImportManifest::Parse, frozen as it ships; this unit reads a 2.x
 * catalog only to report it in the same shape.
 */
struct FMantlePlaceBundleReaderLogic
{
	/** The newest major this reader reads; a manifest of a later one is refused. */
	static constexpr int32 NewestMajor = 3;

	/** Where each layout keeps its manifest, from the bundle's root (spec/compatibility.md §1). */
	static const TCHAR* ManifestPath3() { return TEXT("Manifest.json"); }
	static const TCHAR* ManifestPath2() { return TEXT("Metadata/manifest.json"); }

	/** The refusal's one word for a later major, as the corpus names it. */
	static const TCHAR* MajorAheadRefusal() { return TEXT("major-ahead"); }

	/** The refusal's one word for a 3.x manifest whose frames state a unit, datum, axis order or frame this
	 *  reader does not understand (format.md §4); the message names the value. */
	static const TCHAR* UnreadableRefusal() { return TEXT("unreadable"); }

	/**
	 * Reads the `frames` member of a 3.x manifest (format.md §9.4) as the .NET reader does: the metre, the
	 * international foot and the US survey foot are the units, the local frame is east, north, up and in
	 * metres, a CRS is a code, a PROJJSON object or a WKT2 string. False with `OutError` naming the value
	 * it cannot apply. On success `OutFrames` is the stated object with each `geoid_model` stated.
	 * Nothing is converted or derived.
	 */
	static bool ReadFrames(const TSharedPtr<FJsonObject>& Manifest, TSharedPtr<FJsonObject>& OutFrames, FString& OutError);

	/** Reads one archive entry, by the name the archive lists it under, as text. */
	using FReadText = TFunctionRef<bool(const FString& EntryName, FString& OutText)>;

	/**
	 * Read the bundle whose archive lists `EntryNames` ('/'-separated, as a zip or a folder walk gives
	 * them). Entries that are folders, macOS metadata or not a plain relative path are ignored.
	 */
	static FMantlePlaceBundleRead Read(const TArray<FString>& EntryNames, FReadText ReadText);

	/**
	 * The bundle's root among `EntryNames` and the manifest it holds: the manifest at the top, else
	 * inside the one folder every entry is in. False when there is neither.
	 */
	static bool FindManifest(const TArray<FString>& EntryNames, FString& OutRoot, FString& OutManifestPath);

	/** Whether an archive entry is one the reader considers: a plain relative path to a file. */
	static bool IsReadableEntry(const FString& EntryName);

	/** A 3.x file's family, from its extension alone (format.md §4). */
	static EMantlePlaceBundleFamily FamilyOfExtension(const FString& Path);

	/** A 2.x file's family, from its own frame statement: `absolute` is georeferenced, `offset` host-ready. */
	static EMantlePlaceBundleFamily FamilyOfCoordinateMode(const FString& CoordinateMode);

	static EMantlePlaceBundleFrame FrameOf(EMantlePlaceBundleFamily Family);
	static EMantlePlaceBundleUnit UnitOf(EMantlePlaceBundleFamily Family);

	/** The corpus's words for each value, for logs and the conformance suite. */
	static const TCHAR* FamilyName(EMantlePlaceBundleFamily Family); // "georeferenced", "host-ready", or empty
	static const TCHAR* FrameName(EMantlePlaceBundleFrame Frame);    // "order", "site", or empty
	static const TCHAR* SkipName(EMantlePlaceBundleSkip Why);        // "file-missing" or "not-held"

	/**
	 * The plugin's message for a manifest of a later major: the version it saw, the newest it reads,
	 * and that the plugin needs updating. One text for both readers (MantlePlaceImportManifest::Parse
	 * passes its own newest major).
	 */
	static FString NewerMajorMessage(const FString& Version, int32 ReadsUpToMajor);

	/** The plugin's message for a version that does not parse or is below the floor. */
	static FString BelowFloorMessage(const FString& Version, const FString& Floor);
};
