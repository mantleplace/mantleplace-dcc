// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportTiming.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "Misc/DateTime.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Serialization/JsonWriter.h"

namespace MantlePlaceImportTiming
{
namespace
{
/**
 * The import currently running.
 *
 * A file-static rather than a parameter, for the reason spelled out in the header: the
 * shader stall is measured in another translation unit, and threading a timeline through
 * `ImportLandscape` and everything under it would put a timing argument on signatures that
 * have nothing else to do with timing. It is safe because an import is synchronous, on the
 * editor thread, and serialised by its own `FScopedTransaction`.
 *
 * `FScopedCurrent` saves and restores rather than clearing, so a nested import — which does
 * not happen today and is not forbidden — leaves the outer one intact instead of silently
 * un-instrumenting it.
 */
FTimeline* GCurrent = nullptr;

/** Right-pad, so the seconds column lines up and two runs diff cleanly. */
FString PadTo(const FString& Text, int32 Width)
{
	FString Padded = Text;
	while (Padded.Len() < Width)
	{
		Padded.AppendChar(TEXT(' '));
	}
	return Padded;
}
}

FTimeline* Current()
{
	return GCurrent;
}

void RecordOnCurrent(const TCHAR* InPhase, double InSeconds, FString InDetail)
{
	if (GCurrent != nullptr)
	{
		GCurrent->Record(InPhase, InSeconds, MoveTemp(InDetail));
	}
}

void OpenPhaseOn(FTimeline* Timeline)
{
	if (Timeline != nullptr)
	{
		Timeline->OpenPhase();
	}
}

void ClosePhaseAndRecord(FTimeline* Timeline, const TCHAR* InPhase, double InSeconds, FString InDetail)
{
	if (Timeline == nullptr)
	{
		return;
	}

	// Closed BEFORE the row is recorded, so the row is stamped at the depth it ran at rather than
	// one level inside itself. Everything it enclosed is already in the ledger — a guard reports
	// on the way out, so the deeper rows land first.
	Timeline->ClosePhase();
	Timeline->Record(InPhase, InSeconds, MoveTemp(InDetail));
}

void FAccumulatedPhase::Record()
{
	if (Timeline == nullptr || SpanCount == 0)
	{
		// Nothing to say. A timeline that is already null has recorded; a span count of zero means
		// the loop this was declared for never ran a single iteration, and the vocabulary's rule is
		// that an absent row means "not reached" rather than "instant".
		Timeline = nullptr;
		return;
	}

	// No ClosePhase, and that is the difference from every other guard in this file: this opened no
	// level, so the row lands at the depth the spans ran at rather than one inside itself.
	Timeline->Record(Phase, TotalSeconds, MoveTemp(Detail));
	Timeline = nullptr;
}

FScopedCurrentPhase::FScopedCurrentPhase(const TCHAR* InPhase, FString InDetail)
    : Timeline(GCurrent), Phase(InPhase), Detail(MoveTemp(InDetail)), StartSeconds(FPlatformTime::Seconds())
{
	OpenPhaseOn(Timeline);
}

FScopedCurrentPhase::~FScopedCurrentPhase()
{
	ClosePhaseAndRecord(Timeline, Phase, FPlatformTime::Seconds() - StartSeconds, MoveTemp(Detail));
}

FScopedCurrent::FScopedCurrent(FTimeline& InTimeline)
    : Previous(GCurrent)
{
	GCurrent = &InTimeline;
}

FScopedCurrent::~FScopedCurrent()
{
	GCurrent = Previous;
}

TArray<FString> FTimeline::BuildSummary() const
{
	const double Total = ElapsedSeconds();

	TArray<FString> Lines;
	Lines.Add(TEXT("Mantle Place import timing ----------------------------------------"));

	// A share is only honest against a measured total, and the total is wall time rather than
	// the sum of the phases: the phases do not tile the import, and printing a sum as if they
	// did would quietly claim that everything is accounted for.
	//
	// A nested phase is indented, which is what lets a reader see at a glance that its seconds are
	// already inside the row below it rather than beside it. The indent goes into the label before
	// the width is taken, so the seconds column stays straight.
	int32 LabelWidth = 0;
	TArray<FString> Labels;
	Labels.Reserve(Entries.Num());
	for (const FEntry& Entry : Entries)
	{
		FString Label = FString::ChrN(2 * Entry.Depth, TEXT(' '));
		Label += Entry.Phase != nullptr ? FString(Entry.Phase) : TEXT("(unnamed)");
		if (!Entry.Detail.IsEmpty())
		{
			Label += FString::Printf(TEXT(" [%s]"), *Entry.Detail);
		}
		LabelWidth = FMath::Max(LabelWidth, Label.Len());
		Labels.Add(MoveTemp(Label));
	}

	for (int32 Index = 0; Index < Entries.Num(); ++Index)
	{
		const double Seconds = Entries[Index].Seconds;
		const double Share = Total > 0.0 ? 100.0 * Seconds / Total : 0.0;
		FString Line = FString::Printf(TEXT("  %s  %8.2fs  %5.1f%%"),
		                               *PadTo(Labels[Index], LabelWidth), Seconds, Share);

		// Said on the row itself, not left to the indentation alone. The enclosing row is the one
		// a reader is most likely to add to the rows above it — it is the largest — and this is
		// the line that tells them not to.
		const int32 Enclosed = EnclosedRowCount(Index);
		if (Enclosed > 0)
		{
			Line += FString::Printf(TEXT("   (encloses the %d row(s) above)"), Enclosed);
		}
		Lines.Add(MoveTemp(Line));
	}

	Lines.Add(FString::Printf(TEXT("  %s  %8.2fs  100.0%%"), *PadTo(TEXT("TOTAL (wall)"), LabelWidth), Total));

	// Said out loud rather than left to be inferred from a column that does not add up. The
	// phases are the ones worth naming, not a partition of the import, so unmeasured time is
	// expected — and a reader who does not know that reads a 60% total as a measurement bug.
	//
	// Against the top-level rows only. Counting the nested ones here is what made this number
	// negative on the first run captured off a runner: time inside an enclosing phase would be
	// subtracted from the wall clock twice.
	const double Unmeasured = Total - AccountedSeconds();
	if (Total > 0.0)
	{
		Lines.Add(FString::Printf(
		    TEXT("  %s  %8.2fs  %5.1f%%   (not inside any named phase — the phases are the parts worth naming, not a partition)"),
		    *PadTo(TEXT("unmeasured"), LabelWidth), Unmeasured, 100.0 * Unmeasured / Total));
	}

	Lines.Add(TEXT("-------------------------------------------------------------------"));
	return Lines;
}

FString BuildRecord(const FTimeline& Timeline, const FRecordContext& Context)
{
	FString Json;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);

	Writer->WriteObjectStart();
	Writer->WriteValue(TEXT("schema"), FString(RecordSchema));
	Writer->WriteValue(TEXT("writtenAtUtc"), FDateTime::UtcNow().ToIso8601());
	Writer->WriteValue(TEXT("bundle"), Context.Bundle);
	Writer->WriteValue(TEXT("succeeded"), Context.bSucceeded);

	// Wall time, exactly as the summary block prints it, and NOT the sum of the phases below. The
	// phases do not tile the import; a consumer that added them up would get a different number
	// and no way to tell which one is what the import cost.
	Writer->WriteValue(TEXT("totalWallSeconds"), Timeline.ElapsedSeconds());

	// The entries as recorded, in the order recorded, one object each — including the repeats. A
	// phase that did not run has no entry, which means "not reached" here for the same reason it
	// does in the summary block; a zero would read as "instant".
	//
	// ⛔ `depth` is what makes this array readable, and leaving it out was a real defect rather
	// than an omission of convenience. The rows do not tile the import and they never did, but
	// since nesting was measured they do not even partition it: a row at depth 1 is already inside
	// the row below it at depth 0. A consumer summing `seconds` across the array therefore charges
	// the import twice for the shader stall — the 144% total and the negative remainder the
	// summary block was fixed for. With `depth` the same consumer filters on 0 and reaches
	// `AccountedSeconds()`, which is the number the block prints.
	//
	// The accounted total itself is deliberately NOT written beside it. It is derivable from these
	// rows, and a stored copy is a second answer that can disagree with them.
	Writer->WriteArrayStart(TEXT("phases"));
	for (const FEntry& Entry : Timeline.GetEntries())
	{
		Writer->WriteObjectStart();
		Writer->WriteValue(TEXT("phase"), Entry.Phase != nullptr ? FString(Entry.Phase) : FString(TEXT("(unnamed)")));
		Writer->WriteValue(TEXT("detail"), Entry.Detail);
		Writer->WriteValue(TEXT("seconds"), Entry.Seconds);
		Writer->WriteValue(TEXT("depth"), Entry.Depth);
		Writer->WriteObjectEnd();
	}
	Writer->WriteArrayEnd();

	Writer->WriteObjectEnd();
	Writer->Close();
	return Json;
}

FString RecordDirectoryFromEnvironment()
{
	return FPlatformMisc::GetEnvironmentVariable(RecordDirectoryVariable);
}

FString WriteRecord(const FTimeline& Timeline, const FRecordContext& Context, const FString& Directory)
{
	if (Directory.IsEmpty())
	{
		return FString();
	}

	// Timestamp first so a directory of records sorts chronologically in a file listing, and a
	// short guid after it because two imports in the same second are possible and a collision here
	// would silently discard one of them.
	const FString FileName = FString::Printf(TEXT("ImportTimeline-%s-%s.json"),
	                                         *FDateTime::UtcNow().ToString(TEXT("%Y%m%d-%H%M%S")),
	                                         *FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(8));
	const FString Path = FPaths::Combine(Directory, FileName);

	IFileManager::Get().MakeDirectory(*Directory, /*Tree*/ true);
	if (!FFileHelper::SaveStringToFile(BuildRecord(Timeline, Context), *Path,
	                                   FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		return FString();
	}

	return Path;
}
}
