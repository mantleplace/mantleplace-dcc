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

FScopedCurrentPhase::~FScopedCurrentPhase()
{
	// Resolved at destruction rather than at construction: the answer is the same for the whole
	// scope, and doing it here keeps the guard a plain local with nothing to check on the way in.
	RecordOnCurrent(Phase, FPlatformTime::Seconds() - StartSeconds, MoveTemp(Detail));
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
	int32 LabelWidth = 0;
	TArray<FString> Labels;
	Labels.Reserve(Entries.Num());
	for (const FEntry& Entry : Entries)
	{
		FString Label = Entry.Phase != nullptr ? FString(Entry.Phase) : TEXT("(unnamed)");
		if (!Entry.Detail.IsEmpty())
		{
			Label += FString::Printf(TEXT(" [%s]"), *Entry.Detail);
		}
		LabelWidth = FMath::Max(LabelWidth, Label.Len());
		Labels.Add(MoveTemp(Label));
	}

	double Accounted = 0.0;
	for (int32 Index = 0; Index < Entries.Num(); ++Index)
	{
		const double Seconds = Entries[Index].Seconds;
		Accounted += Seconds;
		const double Share = Total > 0.0 ? 100.0 * Seconds / Total : 0.0;
		Lines.Add(FString::Printf(TEXT("  %s  %8.2fs  %5.1f%%"),
		                          *PadTo(Labels[Index], LabelWidth), Seconds, Share));
	}

	Lines.Add(FString::Printf(TEXT("  %s  %8.2fs  100.0%%"), *PadTo(TEXT("TOTAL (wall)"), LabelWidth), Total));

	// Said out loud rather than left to be inferred from a column that does not add up. The
	// phases are the ones worth naming, not a partition of the import, so unmeasured time is
	// expected — and a reader who does not know that reads a 60% total as a measurement bug.
	const double Unmeasured = Total - Accounted;
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
