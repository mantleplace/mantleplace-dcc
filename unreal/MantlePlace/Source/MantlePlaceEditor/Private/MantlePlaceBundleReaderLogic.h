// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"

class FJsonObject;

enum class EMantlePlaceBundleFamily : uint8
{
	None,
	Georeferenced,
	HostReady,
};

enum class EMantlePlaceBundleFrame : uint8
{
	None,
	Order,
	Site,
};

enum class EMantlePlaceBundleUnit : uint8
{
	None,
	OrderDelivery,
	Metres,
};

struct FMantlePlaceBundleFile
{
	FString Path;
	FString EntryName;
	EMantlePlaceBundleFamily Family = EMantlePlaceBundleFamily::None;
	EMantlePlaceBundleFrame Frame = EMantlePlaceBundleFrame::None;
	EMantlePlaceBundleUnit Unit = EMantlePlaceBundleUnit::None;
	bool bHasCount = false;
	int64 Count = 0;
};

enum class EMantlePlaceBundleSkip : uint8
{
	FileMissing,
	NotHeld,
};

struct FMantlePlaceBundleSkipped
{
	FString Path;
	EMantlePlaceBundleSkip Why = EMantlePlaceBundleSkip::FileMissing;
	FString Reason;
};

enum class EMantlePlaceBundleReadOutcome : uint8
{
	Read,
	Refused,
	Failed,
};

struct FMantlePlaceBundleRead
{
	EMantlePlaceBundleReadOutcome Outcome = EMantlePlaceBundleReadOutcome::Failed;
	FString Refusal;
	FString Error;
	FString Version;
	int32 Major = 0;
	FString ManifestPath;
	FString Root;
	FString Level;
	TSharedPtr<FJsonObject> Frames;
	TArray<FMantlePlaceBundleFile> Files;
	TArray<FMantlePlaceBundleSkipped> Skipped;

	bool IsRead() const { return Outcome == EMantlePlaceBundleReadOutcome::Read; }
};

struct FMantlePlaceBundleReaderLogic
{
	static constexpr int32 NewestMajor = 3;

	static const TCHAR* ManifestPath3() { return TEXT("Manifest.json"); }
	static const TCHAR* ManifestPath2() { return TEXT("Metadata/manifest.json"); }

	static const TCHAR* MajorAheadRefusal() { return TEXT("major-ahead"); }

	static const TCHAR* UnreadableRefusal() { return TEXT("unreadable"); }

	static bool ReadFrames(const TSharedPtr<FJsonObject>& Manifest, TSharedPtr<FJsonObject>& OutFrames, FString& OutError);

	using FReadText = TFunctionRef<bool(const FString& EntryName, FString& OutText)>;

	static FMantlePlaceBundleRead Read(const TArray<FString>& EntryNames, FReadText ReadText);

	static bool FindManifest(const TArray<FString>& EntryNames, FString& OutRoot, FString& OutManifestPath);

	static bool IsReadableEntry(const FString& EntryName);

	static EMantlePlaceBundleFamily FamilyOfExtension(const FString& Path);

	static EMantlePlaceBundleFamily FamilyOfCoordinateMode(const FString& CoordinateMode);

	static EMantlePlaceBundleFrame FrameOf(EMantlePlaceBundleFamily Family);
	static EMantlePlaceBundleUnit UnitOf(EMantlePlaceBundleFamily Family);

	static const TCHAR* FamilyName(EMantlePlaceBundleFamily Family);
	static const TCHAR* FrameName(EMantlePlaceBundleFrame Frame);
	static const TCHAR* SkipName(EMantlePlaceBundleSkip Why);

	static FString NewerMajorMessage(const FString& Version, int32 ReadsUpToMajor);

	static FString BelowFloorMessage(const FString& Version, const FString& Floor);
};
