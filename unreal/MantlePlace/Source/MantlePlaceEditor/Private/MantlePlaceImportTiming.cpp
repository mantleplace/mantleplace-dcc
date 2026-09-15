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
	Writer->WriteArrayStart(TEXT("phases"));
	for (const FEntry& Entry : Timeline.GetEntries())
	{
		Writer->WriteObjectStart();
		Writer->WriteValue(TEXT("phase"), Entry.Phase != nullptr ? FString(Entry.Phase) : FString(TEXT("(unnamed)")));
		Writer->WriteValue(TEXT("detail"), Entry.Detail);
		Writer->WriteValue(TEXT("seconds"), Entry.Seconds);
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
