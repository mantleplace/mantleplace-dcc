// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

namespace MantlePlaceImportTiming
{
namespace Phase
{
inline const TCHAR* const ZipAndManifest = TEXT("zip open + manifest parse");

inline const TCHAR* const IntegrityPrecheck = TEXT("integrity pre-check");

inline const TCHAR* const IntegrityEntryRead = TEXT("integrity entry read");
inline const TCHAR* const IntegrityDigest = TEXT("integrity sha256 digest");

inline const TCHAR* const Wipe = TEXT("wipe (prior import)");

inline const TCHAR* const Artifact = TEXT("artifact");

inline const TCHAR* const LandscapeHeightmapExtract = TEXT("landscape heightmap extract");
inline const TCHAR* const LandscapeHeightmapDecode = TEXT("landscape heightmap decode");
inline const TCHAR* const LandscapeHeightmapOrient = TEXT("landscape heightmap orient");
inline const TCHAR* const LandscapeActorSpawn = TEXT("landscape actor spawn");
inline const TCHAR* const LandscapeLayerInfoAssets = TEXT("landscape layer info assets");
inline const TCHAR* const LandscapeEngineImport = TEXT("landscape engine import");
inline const TCHAR* const LandscapeLayerInfoMap = TEXT("landscape layer info map");
inline const TCHAR* const LandscapeWorldPartitionSplit = TEXT("landscape world partition split");

inline const TCHAR* const ShaderStall = TEXT("shader FinishAllCompilation stall");

inline const TCHAR* const LandscapeMaterialRebuild = TEXT("landscape material instance rebuild");
inline const TCHAR* const LandscapeRenderFlush = TEXT("landscape render flush");

inline const TCHAR* const CesiumAvailability = TEXT("cesium availability rewrite");

inline const TCHAR* const WeightResample = TEXT("weight resample");

inline const TCHAR* const InsideTransaction = TEXT("inside transaction");
}

struct FEntry
{
	const TCHAR* Phase = nullptr;
	FString Detail;
	double Seconds = 0.0;
	int32 Depth = 0;
};

class FTimeline
{
public:
	FTimeline() = default;

	explicit FTimeline(TFunction<double()> InClock)
	    : Clock(MoveTemp(InClock))
	{
	}

	void Start()
	{
		StartSeconds = Clock();
	}

	void Record(const TCHAR* InPhase, double InSeconds, FString InDetail = FString())
	{
		FEntry Entry;
		Entry.Phase = InPhase;
		Entry.Detail = MoveTemp(InDetail);
		Entry.Seconds = InSeconds;
		Entry.Depth = OpenPhaseCount;
		Entries.Add(MoveTemp(Entry));
	}

	void OpenPhase()
	{
		++OpenPhaseCount;
	}

	void ClosePhase()
	{
		OpenPhaseCount = FMath::Max(0, OpenPhaseCount - 1);
	}

	double AccountedSeconds() const
	{
		double Sum = 0.0;
		for (const FEntry& Entry : Entries)
		{
			if (Entry.Depth == 0)
			{
				Sum += Entry.Seconds;
			}
		}
		return Sum;
	}

	double ElapsedSeconds() const
	{
		return StartSeconds > 0.0 ? Clock() - StartSeconds : 0.0;
	}

	const TArray<FEntry>& GetEntries() const
	{
		return Entries;
	}

	int32 EnclosedRowCount(int32 Index) const
	{
		int32 Count = 0;
		for (int32 Back = Index - 1; Back >= 0 && Entries[Back].Depth > Entries[Index].Depth; --Back)
		{
			++Count;
		}
		return Count;
	}

	TArray<FString> BuildSummary() const;

private:
	TArray<FEntry> Entries;
	TFunction<double()> Clock = []()
	{
		return FPlatformTime::Seconds();
	};
	double StartSeconds = 0.0;

	int32 OpenPhaseCount = 0;
};

void OpenPhaseOn(FTimeline* Timeline);
void ClosePhaseAndRecord(FTimeline* Timeline, const TCHAR* InPhase, double InSeconds, FString InDetail);

class FScopedPhase
{
public:
	FScopedPhase(FTimeline& InTimeline, const TCHAR* InPhase, FString InDetail = FString())
	    : Timeline(&InTimeline), Phase(InPhase), Detail(MoveTemp(InDetail)), StartSeconds(FPlatformTime::Seconds())
	{
		OpenPhaseOn(Timeline);
	}

	~FScopedPhase()
	{
		ClosePhaseAndRecord(Timeline, Phase, FPlatformTime::Seconds() - StartSeconds, MoveTemp(Detail));
	}

	FScopedPhase(FScopedPhase&& Other) noexcept
	    : Timeline(Other.Timeline), Phase(Other.Phase), Detail(MoveTemp(Other.Detail)), StartSeconds(Other.StartSeconds)
	{
		Other.Timeline = nullptr;
	}

	FScopedPhase(const FScopedPhase&) = delete;
	FScopedPhase& operator=(const FScopedPhase&) = delete;
	FScopedPhase& operator=(FScopedPhase&&) = delete;

private:
	FTimeline* Timeline;
	const TCHAR* Phase;
	FString Detail;
	double StartSeconds;
};

class FAccumulatedPhase
{
public:
	FAccumulatedPhase(FTimeline& InTimeline, const TCHAR* InPhase)
	    : Timeline(&InTimeline), Phase(InPhase)
	{
	}

	~FAccumulatedPhase()
	{
		Record();
	}

	void Record();

	class FSpan
	{
	public:
		explicit FSpan(FAccumulatedPhase& InOwner)
		    : Owner(&InOwner), StartSeconds(FPlatformTime::Seconds())
		{
		}

		~FSpan()
		{
			Owner->TotalSeconds += FPlatformTime::Seconds() - StartSeconds;
			Owner->bAnySpan = true;
		}

		FSpan(const FSpan&) = delete;
		FSpan& operator=(const FSpan&) = delete;

	private:
		FAccumulatedPhase* Owner;
		double StartSeconds;
	};

	FAccumulatedPhase(const FAccumulatedPhase&) = delete;
	FAccumulatedPhase& operator=(const FAccumulatedPhase&) = delete;

private:
	FTimeline* Timeline;
	const TCHAR* Phase;
	double TotalSeconds = 0.0;

	bool bAnySpan = false;
};

FTimeline* Current();

void RecordOnCurrent(const TCHAR* InPhase, double InSeconds, FString InDetail = FString());

class FScopedCurrentPhase
{
public:
	explicit FScopedCurrentPhase(const TCHAR* InPhase, FString InDetail = FString());

	~FScopedCurrentPhase();

	FScopedCurrentPhase(const FScopedCurrentPhase&) = delete;
	FScopedCurrentPhase& operator=(const FScopedCurrentPhase&) = delete;

private:
	FTimeline* Timeline;
	const TCHAR* Phase;
	FString Detail;
	double StartSeconds;
};

class FScopedCurrent
{
public:
	explicit FScopedCurrent(FTimeline& InTimeline);
	~FScopedCurrent();

	FScopedCurrent(const FScopedCurrent&) = delete;
	FScopedCurrent& operator=(const FScopedCurrent&) = delete;

private:
	FTimeline* Previous;
};

struct FRecordContext
{
	FString Bundle;

	bool bSucceeded = false;
};

inline const TCHAR* const RecordSchema = TEXT("mantleplace.import-timeline/1");

inline const TCHAR* const RecordDirectoryVariable = TEXT("MANTLEPLACE_IMPORT_TIMELINE_DIR");

FString BuildRecord(const FTimeline& Timeline, const FRecordContext& Context);

FString RecordDirectoryFromEnvironment();

FString WriteRecord(const FTimeline& Timeline, const FRecordContext& Context, const FString& Directory);
}
