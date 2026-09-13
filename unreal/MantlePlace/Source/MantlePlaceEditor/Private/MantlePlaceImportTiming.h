// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * Per-phase wall-time for one vault import, and one summary block at the end of it.
 *
 * **Why this exists at all.** Performance bars are set from measurement, relative to a recorded
 * baseline. Unmeasured performance work in an editor plugin burns time moving stalls nobody timed,
 * and an import that takes "a while" is not a number anyone can argue with. This records the
 * numbers; it changes no behaviour, and a patch here that also fixes a stall is the wrong patch.
 *
 * **Why the phase names live in one place.** A timeline is only useful if two runs can be compared,
 * and two runs can only be compared if the phases are spelled identically. Naming them at each log
 * site guarantees they drift the first time someone rewords a string, and the drift is invisible —
 * both runs still print, they just stop lining up. `Phase` below is the whole vocabulary, and a new
 * phase is added there rather than at a call site.
 *
 * **Why it is not behind a verbose flag.** A flag is off on the run that mattered. The cost here is
 * one `FPlatformTime::Seconds()` per phase and a handful of strings per import — against an import
 * measured in minutes — so it is cheap enough to leave on permanently, and that is the point: the
 * next slow import is already instrumented.
 *
 * **Threading.** Editor thread, one import at a time. `ImportVaultPackage` is synchronous and its
 * `FScopedTransaction` already serialises it, which is what makes `FScopedCurrent` safe: a phase
 * measured in another translation unit — the landscape's shader stall lives in
 * `MantlePlaceLandscapeImporter.cpp` — reports into the import that is running, without threading a
 * timeline parameter through signatures that have nothing else to do with timing.
 */
namespace MantlePlaceImportTiming
{
/**
 * The phase vocabulary. One place, so two runs line up.
 *
 * Order here is the order they occur, and it is the order the summary prints. A phase that does
 * not run in a given import simply has no row — an absent row means "not reached", which is
 * information, where a zero would read as "instant".
 */
namespace Phase
{
/** Opening the zip and parsing `Metadata/manifest.json`. */
inline const TCHAR* const ZipAndManifest = TEXT("zip open + manifest parse");

/** The fail-closed sha256 pre-check over the declared payload chain. */
inline const TCHAR* const IntegrityPrecheck = TEXT("integrity pre-check");

/** The idempotent wipe of a prior import's assets and actors. */
inline const TCHAR* const Wipe = TEXT("wipe (prior import)");

/** One per imported artifact. The artifact's own name is the detail, not the phase. */
inline const TCHAR* const Artifact = TEXT("artifact");

/** ⛔ Hotspot 1: the in-transaction shader-compile stall the editor blocks on. */
inline const TCHAR* const ShaderStall = TEXT("shader FinishAllCompilation stall");

/** ⛔ Hotspot 2: `RewriteCesiumTerrainAvailability`'s recursive scan and JSON rewrite. */
inline const TCHAR* const CesiumAvailability = TEXT("cesium availability rewrite");

/** ⛔ Hotspot 3: nearest-neighbour weight resampling. */
inline const TCHAR* const WeightResample = TEXT("weight resample");

/** Closing the import transaction. */
inline const TCHAR* const TransactionClose = TEXT("transaction close");
}

/** One measured phase. `Detail` distinguishes repeated phases — which artifact, which stream. */
struct FEntry
{
	const TCHAR* Phase = nullptr;
	FString Detail;
	double Seconds = 0.0;
};

/**
 * The ledger for one import.
 *
 * Owned by `ImportVaultPackage` and emitted once, at the end, as a single block. One block
 * rather than a running commentary is deliberate: a reader comparing two imports wants both
 * timelines side by side, and a summary interleaved with the importer's own progress lines is
 * not something anyone can diff.
 */
class FTimeline
{
public:
	void Start()
	{
		StartSeconds = FPlatformTime::Seconds();
	}

	void Record(const TCHAR* InPhase, double InSeconds, FString InDetail = FString())
	{
		Entries.Add(FEntry{ InPhase, MoveTemp(InDetail), InSeconds });
	}

	/** Wall time from `Start()` to now — the denominator every share is taken against. */
	double ElapsedSeconds() const
	{
		return StartSeconds > 0.0 ? FPlatformTime::Seconds() - StartSeconds : 0.0;
	}

	/** Every recorded phase, in the order it was recorded. */
	const TArray<FEntry>& GetEntries() const
	{
		return Entries;
	}

	/**
	 * The summary block, as lines. Returned rather than logged so it can be asserted headlessly
	 * without capturing the log — the property worth testing is the shape of the block, and a
	 * test that greps `UE_LOG` output tests the log system instead.
	 */
	TArray<FString> BuildSummary() const;

private:
	TArray<FEntry> Entries;
	double StartSeconds = 0.0;
};

/** Measures one phase and records it on the way out, on whichever path leaves the scope. */
class FScopedPhase
{
public:
	FScopedPhase(FTimeline& InTimeline, const TCHAR* InPhase, FString InDetail = FString())
	    : Timeline(&InTimeline), Phase(InPhase), Detail(MoveTemp(InDetail)), StartSeconds(FPlatformTime::Seconds())
	{
	}

	~FScopedPhase()
	{
		if (Timeline != nullptr)
		{
			Timeline->Record(Phase, FPlatformTime::Seconds() - StartSeconds, MoveTemp(Detail));
		}
	}

	/**
	 * Movable, so a phase whose extent is not a lexical block can live in a TOptional — the zip +
	 * manifest phase opens before the reader it measures and closes after the manifest is parsed,
	 * with early returns in between, and neither a plain block nor a plain local expresses that.
	 * The moved-from guard is disarmed rather than left pointing at the timeline, which is what
	 * stops one phase being recorded twice.
	 */
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

/**
 * The timeline of the import currently running, or `nullptr` when none is.
 *
 * ⛔ Always null-check it. A caller in another translation unit may be reached outside an
 * import — `MantlePlaceLandscapeImporter` is also driven by tests — and a timing call must never
 * be the reason one of those crashes. `RecordOnCurrent` below is the null-safe form and is what
 * call sites should use.
 */
FTimeline* Current();

/** Record against the running import, or do nothing when there is not one. */
void RecordOnCurrent(const TCHAR* InPhase, double InSeconds, FString InDetail = FString());

/** Publishes a timeline as the current one for the duration of the scope. */
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
}
