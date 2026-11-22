// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

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
FTimeline* GCurrent = nullptr;

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

	Timeline->ClosePhase();
	Timeline->Record(InPhase, InSeconds, MoveTemp(InDetail));
}

void FAccumulatedPhase::Record()
{
	if (Timeline == nullptr || !bAnySpan)
	{
		Timeline = nullptr;
		return;
	}

	Timeline->Record(Phase, TotalSeconds);
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

		const int32 Enclosed = EnclosedRowCount(Index);
		if (Enclosed > 0)
		{
			Line += FString::Printf(TEXT("   (encloses the %d row(s) above)"), Enclosed);
		}
		Lines.Add(MoveTemp(Line));
	}

	Lines.Add(FString::Printf(TEXT("  %s  %8.2fs  100.0%%"), *PadTo(TEXT("TOTAL (wall)"), LabelWidth), Total));

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

	Writer->WriteValue(TEXT("totalWallSeconds"), Timeline.ElapsedSeconds());

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

	const FString FileName = FString::Printf(TEXT("ImportTimeline-%s-%s.json"),
	                                         *FDateTime::UtcNow().ToString(TEXT("%Y%m%d-%H%M%S")),
	                                         *FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(8));
	const FString Path = FPaths::Combine(Directory, FileName);

	IFileManager::Get().MakeDirectory(*Directory, true);
	if (!FFileHelper::SaveStringToFile(BuildRecord(Timeline, Context), *Path,
	                                   FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		return FString();
	}

	return Path;
}
}
