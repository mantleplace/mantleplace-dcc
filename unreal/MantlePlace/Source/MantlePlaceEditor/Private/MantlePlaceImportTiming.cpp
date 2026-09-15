// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportTiming.h"

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
}
