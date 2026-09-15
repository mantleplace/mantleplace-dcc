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
 * **Why phases nest, and why the ledger knows it.** Some phases run inside others: the shader stall
 * is inside the landscape artifact, and every artifact imported under the transaction is inside it.
 * The first summary ever read off a runner added all of them together, so its shares totalled 144%
 * and its remainder printed negative. A row is therefore stamped with how many phases were open
 * when it was recorded, and only the top-level rows are summed. The nested rows are still printed —
 * the stall is a hotspot, and dropping it to make a column add up would hide the number the whole
 * exercise exists to find.
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

/**
 * The span inside the import transaction — an ENCLOSING phase: every artifact imported under the
 * transaction is recorded inside it.
 *
 * ⚠ It was called "transaction close" until it was first read off a runner, and that name was
 * wrong in a way the number hid: the guard is declared at function scope, so it spans the whole
 * transaction rather than the gap before the commit. The commit itself is not measured at all —
 * sizing it needs a guard inside `FScopedTransaction`, which is engine code — so a row named for
 * the close would be naming the one thing it does not contain.
 */
inline const TCHAR* const InsideTransaction = TEXT("inside transaction");
}

/**
 * One measured phase.
 *
 * `Detail` distinguishes repeated phases — which artifact, which stream. `Depth` is how many
 * phases were already open when this one was recorded: `0` is a phase of the import itself, `1` a
 * phase inside one of those, and so on.
 */
struct FEntry
{
	const TCHAR* Phase = nullptr;
	FString Detail;
	double Seconds = 0.0;
	int32 Depth = 0;
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
		// Field by field rather than as an aggregate: a row carries four things now, and a
		// positional brace list is correct right up until someone inserts a member.
		FEntry Entry;
		Entry.Phase = InPhase;
		Entry.Detail = MoveTemp(InDetail);
		Entry.Seconds = InSeconds;
		Entry.Depth = OpenPhaseCount;
		Entries.Add(MoveTemp(Entry));
	}

	/**
	 * Open and close a nesting level.
	 *
	 * ⛔ The scope guards below pair these; a call site does not. Depth is **measured** rather than
	 * declared precisely because the phase that actually nests does not know that it does — the
	 * shader stall is a bare `RecordOnCurrent` from the landscape importer, several frames of
	 * stack below the artifact phase enclosing it, and no argument at that call site could say so.
	 */
	void OpenPhase()
	{
		++OpenPhaseCount;
	}

	/**
	 * ⚠ Clamped rather than checked. An unbalanced close cannot happen through the guards, which
	 * are the only intended callers — and this file's standing rule is that a timing call is never
	 * the reason an import dies (see `RecordOnCurrent`). A `check()` here would trade a wrong
	 * number for a lost import, which is the wrong way round for measurement code.
	 */
	void ClosePhase()
	{
		OpenPhaseCount = FMath::Max(0, OpenPhaseCount - 1);
	}

	/**
	 * The import's own phases, summed — the part of the wall time that is accounted for.
	 *
	 * ⛔ Nested rows are deliberately left out. A phase recorded inside another is already part of
	 * it, so adding both charges the import twice for the same seconds: that is what made the
	 * summary's shares total 144% and its remainder print negative on the first run ever captured
	 * off a runner.
	 */
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
	 * How many rows the row at `Index` encloses.
	 *
	 * They are exactly the rows immediately above it: a guard reports on the way out, so
	 * everything it enclosed is already in the ledger, and the run ends at the first row that is
	 * not deeper than it.
	 *
	 * On the ledger rather than beside the printer so it can be asserted for what it is — a count —
	 * instead of by searching the rendered block for the sentence it produces.
	 */
	int32 EnclosedRowCount(int32 Index) const
	{
		int32 Count = 0;
		for (int32 Back = Index - 1; Back >= 0 && Entries[Back].Depth > Entries[Index].Depth; --Back)
		{
			++Count;
		}
		return Count;
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

	/** How many phases are open right now. Stamped onto every row as its `Depth`. */
	int32 OpenPhaseCount = 0;
};

/**
 * Open a nesting level, and — its counterpart — close that level and record the phase.
 *
 * ⛔ The ordering rule lives here and nowhere else: the level is closed BEFORE the row is
 * recorded, so the row lands at the depth it ran at rather than one level inside itself. Both
 * guards below call these rather than repeating the sequence, because a second copy of an ordering
 * rule is a second place for it to go wrong.
 *
 * Both are null-safe on the timeline, so a guard that never found an import to attach to opens
 * nothing and records nothing — and, crucially, closes nothing either.
 */
void OpenPhaseOn(FTimeline* Timeline);
void ClosePhaseAndRecord(FTimeline* Timeline, const TCHAR* InPhase, double InSeconds, FString InDetail);

/** Measures one phase and records it on the way out, on whichever path leaves the scope. */
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

	/**
	 * Movable, so a phase whose extent is not a lexical block can live in a TOptional — the zip +
	 * manifest phase opens before the reader it measures and closes after the manifest is parsed,
	 * with early returns in between, and neither a plain block nor a plain local expresses that.
	 * The moved-from guard is disarmed rather than left pointing at the timeline, which is what
	 * stops one phase being recorded twice — and, since the level it opened is closed by whichever
	 * guard still owns it, stops that level being closed twice as well.
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

/**
 * Measures one phase against whichever import is running, and records it on the way out.
 *
 * The counterpart to FScopedPhase, for a phase that is NOT inside ImportVaultPackage's own scope.
 * Streaming a Cesium terrain is one: it happens in its own function, reached from the import but
 * with no timeline in scope, and the alternative was a timing parameter on a signature that has
 * nothing else to do with timing. Null-safe for the same reason RecordOnCurrent is — that function
 * is also reachable from tests, where no import is running.
 */
class FScopedCurrentPhase
{
public:
	/**
	 * Out of line, because it resolves the running import and the file-static holding it lives in
	 * the .cpp. The level has to be opened here rather than at destruction: anything this phase
	 * records inside itself has to see it already open.
	 */
	explicit FScopedCurrentPhase(const TCHAR* InPhase, FString InDetail = FString());

	~FScopedCurrentPhase();

	FScopedCurrentPhase(const FScopedCurrentPhase&) = delete;
	FScopedCurrentPhase& operator=(const FScopedCurrentPhase&) = delete;

private:
	/**
	 * ⛔ Resolved once, on the way in, and held — not looked up again on the way out.
	 *
	 * Looking it up twice lets the two ends disagree: no import current at construction and one
	 * current at destruction closes a level that was never opened, and because `ClosePhase`
	 * clamps rather than complains, every row after it is stamped one level too shallow and the
	 * double count comes back silently. Holding the pointer makes the pair structural instead of
	 * conditional. It cannot dangle: a timeline is a local of the import that published it, and
	 * this guard is always inside that scope.
	 */
	FTimeline* Timeline;
	const TCHAR* Phase;
	FString Detail;
	double StartSeconds;
};

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

/**
 * The machine-readable half of the timeline, for a reader that is not a person.
 *
 * `BuildSummary` is a block meant to be read: a prose header, a right-padded label column, a share
 * column. Every one of those is presentation, and the first reword breaks anything parsing it
 * without breaking anything a person notices — the numbers still print, they just stop being found.
 * So a consumer outside this process gets a record instead, and the two audiences stop sharing one
 * format neither is well served by.
 *
 * ⛔ **The record is un-thresholded, and this is not a detail.** It carries what was measured and
 * nothing about what the measurement ought to have been. This plugin is world-readable; a bar
 * committed here would state, permanently and in history, what is considered acceptable import cost
 * on hardware this repository does not describe. What counts as a good timeline is a judgement, it
 * depends on a machine and a bundle neither of which is here, and it belongs to whoever consumes
 * this file. A headless test asserts the absence, because the edit that adds an "expected" column
 * is one line and reads as harmless in a diff.
 */

/** What a record carries beyond the phases themselves. */
struct FRecordContext
{
	/**
	 * The bundle's file NAME, never its path. A path names a machine's directory layout, and this
	 * record is written to be collected as a build artifact by whatever asked for it.
	 */
	FString Bundle;

	/**
	 * Whether the import this timeline measured reported success.
	 *
	 * An import that failed after four minutes is a different measurement from one that took four
	 * minutes, and a consumer that cannot tell them apart will eventually average them.
	 */
	bool bSucceeded = false;
};

/**
 * The shape name every record carries, so a consumer can refuse a shape it does not know rather
 * than read a field that has moved. Bump it when a field changes meaning, not when one is added.
 *
 * ⚠ `depth` was added to each phase entry without a bump, under that rule. It is worth saying why,
 * because the array did become unsummable and this looks at first like the meaning changing: the
 * rows started nesting when nesting began to be measured, and the schema was not bumped then
 * either. Nothing a `/1` reader already read means anything different — `phase`, `detail` and
 * `seconds` are untouched, and a reader that ignores unknown fields is exactly as correct, or as
 * wrong, as it was before. `depth` is the recovery of a fact the record had been dropping, not a
 * new contract. A bump here would only force every consumer to re-pin for no semantic reason.
 */
inline const TCHAR* const RecordSchema = TEXT("mantleplace.import-timeline/1");

/**
 * The environment variable naming the directory records are written into.
 *
 * Opt-in, and unset by default: a plugin that scatters JSON into a curator's project earns a bug
 * report about a folder nobody asked for. Something that wants the records says where.
 */
inline const TCHAR* const RecordDirectoryVariable = TEXT("MANTLEPLACE_IMPORT_TIMELINE_DIR");

/** One timeline as one JSON object. Pure — no file system, no environment. */
FString BuildRecord(const FTimeline& Timeline, const FRecordContext& Context);

/** The directory named by `MANTLEPLACE_IMPORT_TIMELINE_DIR`, or empty when it is unset. */
FString RecordDirectoryFromEnvironment();

/**
 * Write one record into `Directory`, and return the file written.
 *
 * Returns empty when `Directory` is empty — the opt-out path, and not a failure — and also when the
 * write fails, which is why the caller checks the directory itself before deciding what to say. The
 * directory is created if it does not exist: a consumer naming a path ahead of the run that fills
 * it is the normal case.
 *
 * The file name is unique per call rather than fixed. One import per run is today's shape and not a
 * guarantee, and a second import overwriting the first would leave a consumer grading the wrong one
 * with nothing to notice.
 */
FString WriteRecord(const FTimeline& Timeline, const FRecordContext& Context, const FString& Directory);
}
