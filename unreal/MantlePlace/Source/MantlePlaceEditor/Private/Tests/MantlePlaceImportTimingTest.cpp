// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "HAL/PlatformProcess.h"
#include "MantlePlaceImportTiming.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportTimingTest,
    "MantlePlace.Import.Timing",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
int32 RowsMentioning(const TArray<FString>& Lines, const TCHAR* Needle)
{
	int32 Count = 0;
	for (const FString& Line : Lines)
	{
		if (Line.Contains(Needle))
		{
			++Count;
		}
	}
	return Count;
}

TSharedPtr<FJsonObject> ParseRecord(const FString& Json)
{
	TSharedPtr<FJsonObject> Object;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	return FJsonSerializer::Deserialize(Reader, Object) ? Object : nullptr;
}

FString ScratchDirectory()
{
	return FPaths::Combine(FPlatformProcess::UserTempDir(),
	                       FString::Printf(TEXT("MantlePlaceTimelineTest-%s"),
	                                       *FGuid::NewGuid().ToString(EGuidFormats::Digits)));
}
}

bool FMantlePlaceImportTimingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportTiming;

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);
		Timeline.Record(Phase::IntegrityPrecheck, 2.0);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("one row per phase: zip"), RowsMentioning(Summary, Phase::ZipAndManifest), 1);
		TestEqual(TEXT("one row per phase: integrity"), RowsMentioning(Summary, Phase::IntegrityPrecheck), 1);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Wipe, 0.5, TEXT("assets"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("one header"), RowsMentioning(Summary, TEXT("Mantle Place import timing")), 1);
		TestEqual(TEXT("one total"), RowsMentioning(Summary, TEXT("TOTAL (wall)")), 1);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Artifact, 1.0, TEXT("terrain mesh (glTF, Nanite)"));
		Timeline.Record(Phase::Artifact, 2.0, TEXT("buildings mesh (glTF)"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("two artifact rows"), RowsMentioning(Summary, Phase::Artifact), 2);
		TestEqual(TEXT("the first names its artifact"), RowsMentioning(Summary, TEXT("terrain mesh")), 1);
		TestEqual(TEXT("the second names its artifact"), RowsMentioning(Summary, TEXT("buildings mesh")), 1);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ShaderStall, 30.0);
		Timeline.Record(Phase::CesiumAvailability, 5.0, TEXT("400 entries"));
		Timeline.Record(Phase::WeightResample, 8.0, TEXT("2 plane(s)"));

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("shader stall row"), RowsMentioning(Summary, Phase::ShaderStall), 1);
		TestEqual(TEXT("cesium rewrite row"), RowsMentioning(Summary, Phase::CesiumAvailability), 1);
		TestEqual(TEXT("weight resample row"), RowsMentioning(Summary, Phase::WeightResample), 1);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("an unrun phase has no row"), RowsMentioning(Summary, Phase::ShaderStall), 0);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		FPlatformProcess::Sleep(0.001f);
		Timeline.Record(Phase::ZipAndManifest, 1000.0);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("the remainder is named"), RowsMentioning(Summary, TEXT("unmeasured")), 1);
		TestTrue(TEXT("wall time is not the sum"), Timeline.ElapsedSeconds() < 1000.0);
	}

	{
		TestNull(TEXT("no import is current here"), Current());
		RecordOnCurrent(Phase::ShaderStall, 1.0);
	}

	{
		FTimeline Outer;
		Outer.Start();
		{
			FScopedCurrent PublishOuter(Outer);
			TestEqual(TEXT("the outer timeline is current"), Current(), &Outer);

			FTimeline Inner;
			Inner.Start();
			{
				FScopedCurrent PublishInner(Inner);
				TestEqual(TEXT("the inner timeline takes over"), Current(), &Inner);
				RecordOnCurrent(Phase::ShaderStall, 3.0);
			}
			TestEqual(TEXT("the outer timeline is restored"), Current(), &Outer);
			TestEqual(TEXT("the inner timeline got the record"), Inner.GetEntries().Num(), 1);
		}
		TestNull(TEXT("nothing is current after the outermost scope"), Current());
		TestEqual(TEXT("the outer timeline got nothing"), Outer.GetEntries().Num(), 0);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			{
				const FScopedCurrentPhase Measured(Phase::CesiumAvailability, TEXT("400 entries"));
			}
		}
		TestEqual(TEXT("it recorded against the running import"), Timeline.GetEntries().Num(), 1);
		if (Timeline.GetEntries().Num() == 1)
		{
			TestEqual(TEXT("it carries its detail"), Timeline.GetEntries()[0].Detail, FString(TEXT("400 entries")));
		}
	}

	{
		TestNull(TEXT("no import is current here"), Current());
		const FScopedCurrentPhase Measured(Phase::CesiumAvailability);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("terrain mesh (glTF, Nanite)"));
			{
				const FScopedCurrentPhase Measured(Phase::CesiumAvailability, TEXT("400 entries"));
			}
		}

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the rewrite is nested in the artifact"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the artifact phase is top level"), Timeline.GetEntries()[1].Depth, 0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the enclosed row is deeper"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the enclosing row is top level"), Timeline.GetEntries()[1].Depth, 0);
		}

		TestEqual(TEXT("an enclosed row is not counted again"), Timeline.AccountedSeconds(), 10.0);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		TestEqual(TEXT("the nested row encloses nothing"), Timeline.EnclosedRowCount(0), 0);
		TestEqual(TEXT("the enclosing row encloses one"), Timeline.EnclosedRowCount(1), 1);

		const TArray<FString> Summary = Timeline.BuildSummary();
		TestEqual(TEXT("the stall is still a row"), RowsMentioning(Summary, Phase::ShaderStall), 1);
		TestEqual(TEXT("the enclosing row is named as such"), RowsMentioning(Summary, TEXT("encloses")), 1);

		int32 StallIndent = -1;
		int32 ArtifactIndent = -1;
		for (const FString& Line : Summary)
		{
			const int32 Indent = Line.Len() - Line.TrimStart().Len();
			if (Line.Contains(Phase::ShaderStall))
			{
				StallIndent = Indent;
			}
			else if (Line.Contains(TEXT("landscape 2017x2017")))
			{
				ArtifactIndent = Indent;
			}
		}
		TestTrue(TEXT("the nested row is indented further"), StallIndent > ArtifactIndent);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("landscape 2017x2017"));
			RecordOnCurrent(Phase::ShaderStall, 3.0);
		}

		TestEqual(TEXT("both rows were recorded"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the stall is nested without asking to be"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the artifact phase is top level"), Timeline.GetEntries()[1].Depth, 0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		FPlatformProcess::Sleep(0.05f);

		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 0.03);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 0.04, TEXT("landscape 2017x2017"));

		const double Remainder = Timeline.ElapsedSeconds() - Timeline.AccountedSeconds();
		TestTrue(TEXT("the remainder is not negative"), Remainder >= 0.0);
	}

	{
		FTimeline Timeline;
		Timeline.Start();

		TestNull(TEXT("no import is current yet"), Current());
		TOptional<FScopedCurrentPhase> Straggler;
		Straggler.Emplace(Phase::CesiumAvailability, TEXT("400 entries"));

		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase ArtifactPhase(Timeline, Phase::Artifact, TEXT("landscape 2017x2017"));

			Straggler.Reset();

			RecordOnCurrent(Phase::ShaderStall, 3.0);
		}

		TestEqual(TEXT("the straggler recorded nothing"),
		          RowsMentioning(Timeline.BuildSummary(), Phase::CesiumAvailability), 0);
		TestEqual(TEXT("two rows, not three"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the stall is still nested"), Timeline.GetEntries()[0].Depth, 1);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			const FScopedPhase Outer(Timeline, Phase::Wipe, TEXT("assets"));
			{
				const FScopedPhase Inner(Timeline, Phase::Artifact, TEXT("buildings mesh (glTF)"));
			}
		}

		TestEqual(TEXT("two rows"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the inner guard is nested"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the outer guard is not"), Timeline.GetEntries()[1].Depth, 0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			const FScopedPhase Measured(Timeline, Phase::Wipe, TEXT("assets"));
			TestEqual(TEXT("nothing is recorded until the scope ends"), Timeline.GetEntries().Num(), 0);
		}
		TestEqual(TEXT("exactly one entry after the scope"), Timeline.GetEntries().Num(), 1);
		if (Timeline.GetEntries().Num() == 1)
		{
			TestEqual(TEXT("it carries its detail"), Timeline.GetEntries()[0].Detail, FString(TEXT("assets")));
			TestTrue(TEXT("it carries a non-negative duration"), Timeline.GetEntries()[0].Seconds >= 0.0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FAccumulatedPhase Accumulated(Timeline, Phase::IntegrityDigest);
			for (int32 Index = 0; Index < 3; ++Index)
			{
				const FAccumulatedPhase::FSpan Span(Accumulated);
				FPlatformProcess::Sleep(0.01f);
			}
			TestEqual(TEXT("nothing is recorded until the accumulator ends"), Timeline.GetEntries().Num(), 0);
		}

		TestEqual(TEXT("three spans, one row"), Timeline.GetEntries().Num(), 1);
		if (Timeline.GetEntries().Num() == 1)
		{
			TestTrue(TEXT("the seconds are a sum, not one span"), Timeline.GetEntries()[0].Seconds >= 0.01);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FAccumulatedPhase Accumulated(Timeline, Phase::IntegrityEntryRead);
		}
		TestEqual(TEXT("no spans, no row"), Timeline.GetEntries().Num(), 0);
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FAccumulatedPhase ReadPhase(Timeline, Phase::IntegrityEntryRead);
			FAccumulatedPhase DigestPhase(Timeline, Phase::IntegrityDigest);
			{
				const FAccumulatedPhase::FSpan ReadSpan(ReadPhase);
			}
			{
				const FAccumulatedPhase::FSpan DigestSpan(DigestPhase);
			}

			ReadPhase.Record();
			DigestPhase.Record();
			TestEqual(TEXT("both rows are in before the scope ends"), Timeline.GetEntries().Num(), 2);
		}

		TestEqual(TEXT("the destructors added nothing"), Timeline.GetEntries().Num(), 2);
		if (Timeline.GetEntries().Num() == 2)
		{
			TestEqual(TEXT("the reading row is first, as it ran"), FString(Timeline.GetEntries()[0].Phase),
			          FString(Phase::IntegrityEntryRead));
			TestEqual(TEXT("the hashing row is second"), FString(Timeline.GetEntries()[1].Phase),
			          FString(Phase::IntegrityDigest));
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase Enclosing(Timeline, Phase::IntegrityPrecheck);

			FAccumulatedPhase ReadPhase(Timeline, Phase::IntegrityEntryRead);
			{
				const FAccumulatedPhase::FSpan ReadSpan(ReadPhase);
				RecordOnCurrent(Phase::ShaderStall, 1.0);
			}
			ReadPhase.Record();
		}

		TestEqual(TEXT("three rows"), Timeline.GetEntries().Num(), 3);
		if (Timeline.GetEntries().Num() == 3)
		{
			TestEqual(TEXT("a row recorded inside a span is not deepened by it"), Timeline.GetEntries()[0].Depth, 1);
			TestEqual(TEXT("the accumulated row sits inside the enclosing phase"), Timeline.GetEntries()[1].Depth, 1);
			TestEqual(TEXT("the enclosing phase is top level"), Timeline.GetEntries()[2].Depth, 0);

			TestEqual(TEXT("only the enclosing row is accounted"), Timeline.AccountedSeconds(),
			          Timeline.GetEntries()[2].Seconds);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		{
			FScopedCurrent Publish(Timeline);
			const FScopedPhase TransactionPhase(Timeline, Phase::InsideTransaction);
			const FScopedPhase LandscapePhase(Timeline, Phase::Artifact, TEXT("landscape 2017x2017"));
			{
				const FScopedCurrentPhase DecodePhase(Phase::LandscapeHeightmapDecode);
			}
			{
				const FScopedCurrentPhase EngineImportPhase(Phase::LandscapeEngineImport);
			}
			RecordOnCurrent(Phase::ShaderStall, 1.0);
		}

		TestEqual(TEXT("five rows"), Timeline.GetEntries().Num(), 5);
		if (Timeline.GetEntries().Num() == 5)
		{
			TestEqual(TEXT("the decode is inside the landscape"), Timeline.GetEntries()[0].Depth, 2);
			TestEqual(TEXT("so is the engine import"), Timeline.GetEntries()[1].Depth, 2);
			TestEqual(TEXT("and the stall is at the same level as both"), Timeline.GetEntries()[2].Depth, 2);
			TestEqual(TEXT("the landscape artifact is inside the transaction"), Timeline.GetEntries()[3].Depth, 1);
			TestEqual(TEXT("the transaction is top level"), Timeline.GetEntries()[4].Depth, 0);

			TestEqual(TEXT("the landscape encloses its three steps"), Timeline.EnclosedRowCount(3), 3);
			TestEqual(TEXT("the transaction encloses all four"), Timeline.EnclosedRowCount(4), 4);
		}
	}

	{
		const TCHAR* const Everything[] = {Phase::ZipAndManifest,
		                                   Phase::IntegrityPrecheck,
		                                   Phase::IntegrityEntryRead,
		                                   Phase::IntegrityDigest,
		                                   Phase::Wipe,
		                                   Phase::Artifact,
		                                   Phase::LandscapeHeightmapExtract,
		                                   Phase::LandscapeHeightmapDecode,
		                                   Phase::LandscapeHeightmapOrient,
		                                   Phase::LandscapeActorSpawn,
		                                   Phase::LandscapeLayerInfoAssets,
		                                   Phase::LandscapeEngineImport,
		                                   Phase::LandscapeLayerInfoMap,
		                                   Phase::ShaderStall,
		                                   Phase::LandscapeMaterialRebuild,
		                                   Phase::LandscapeRenderFlush,
		                                   Phase::CesiumAvailability,
		                                   Phase::WeightResample,
		                                   Phase::InsideTransaction};

		TSet<FString> Seen;
		for (const TCHAR* const Name : Everything)
		{
			TestFalse(*FString::Printf(TEXT("phase name '%s' is used once"), Name), Seen.Contains(FString(Name)));
			Seen.Add(FString(Name));
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);

		FRecordContext Context;
		Context.Bundle = TEXT("SomeBundle.zip");
		Context.bSucceeded = true;

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, Context));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestEqual(TEXT("it names its schema"), Record->GetStringField(TEXT("schema")), FString(RecordSchema));
			TestEqual(TEXT("it carries the bundle's name"), Record->GetStringField(TEXT("bundle")), FString(TEXT("SomeBundle.zip")));
			TestTrue(TEXT("it carries the outcome"), Record->GetBoolField(TEXT("succeeded")));
			TestTrue(TEXT("it is stamped"), !Record->GetStringField(TEXT("writtenAtUtc")).IsEmpty());
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Artifact, 1.25, TEXT("terrain mesh (glTF, Nanite)"));
		Timeline.Record(Phase::Artifact, 2.50, TEXT("buildings mesh (glTF)"));

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			const TArray<TSharedPtr<FJsonValue>>& Phases = Record->GetArrayField(TEXT("phases"));
			TestEqual(TEXT("two phases recorded, two entries written"), Phases.Num(), 2);
			if (Phases.Num() == 2)
			{
				TestEqual(TEXT("the first keeps its phase"), Phases[0]->AsObject()->GetStringField(TEXT("phase")), FString(Phase::Artifact));
				TestEqual(TEXT("the first keeps its detail"), Phases[0]->AsObject()->GetStringField(TEXT("detail")), FString(TEXT("terrain mesh (glTF, Nanite)")));
				TestEqual(TEXT("the first keeps its seconds"), Phases[0]->AsObject()->GetNumberField(TEXT("seconds")), 1.25);
				TestEqual(TEXT("the second is the second"), Phases[1]->AsObject()->GetStringField(TEXT("detail")), FString(TEXT("buildings mesh (glTF)")));
			}
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			const TArray<TSharedPtr<FJsonValue>>& Phases = Record->GetArrayField(TEXT("phases"));
			TestEqual(TEXT("both rows are written, the nested one included"), Phases.Num(), 2);
			if (Phases.Num() == 2)
			{
				TestEqual(TEXT("the enclosed row is written as nested"),
				    Phases[0]->AsObject()->GetIntegerField(TEXT("depth")), 1);
				TestEqual(TEXT("the enclosing row is written as top level"),
				    Phases[1]->AsObject()->GetIntegerField(TEXT("depth")), 0);
			}
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1.0);
		Timeline.OpenPhase();
		Timeline.Record(Phase::ShaderStall, 3.0);
		Timeline.ClosePhase();
		Timeline.Record(Phase::Artifact, 10.0, TEXT("landscape 2017x2017"));

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			double TopLevel = 0.0;
			double EveryRow = 0.0;
			for (const TSharedPtr<FJsonValue>& Value : Record->GetArrayField(TEXT("phases")))
			{
				const TSharedPtr<FJsonObject> Row = Value->AsObject();
				EveryRow += Row->GetNumberField(TEXT("seconds"));
				if (Row->GetIntegerField(TEXT("depth")) == 0)
				{
					TopLevel += Row->GetNumberField(TEXT("seconds"));
				}
			}

			TestEqual(TEXT("depth-0 rows sum to the ledger's accounted total"), TopLevel, Timeline.AccountedSeconds());
			TestEqual(TEXT("which is 11.0 - the stall is inside the landscape, not beside it"), TopLevel, 11.0);
			TestEqual(TEXT("summing every row reaches 14.0, the double count depth exists to prevent"), EveryRow, 14.0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ZipAndManifest, 1000.0);

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, FRecordContext()));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestTrue(TEXT("the total is wall time, not the sum"), Record->GetNumberField(TEXT("totalWallSeconds")) < 1000.0);
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();

		FRecordContext Context;
		Context.bSucceeded = false;

		const TSharedPtr<FJsonObject> Record = ParseRecord(BuildRecord(Timeline, Context));
		TestNotNull(TEXT("the record parses as JSON"), Record.Get());
		if (Record.IsValid())
		{
			TestEqual(TEXT("no phases, no entries"), Record->GetArrayField(TEXT("phases")).Num(), 0);
			TestFalse(TEXT("a failed import says so"), Record->GetBoolField(TEXT("succeeded")));
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::ShaderStall, 30.0);

		const FString Json = BuildRecord(Timeline, FRecordContext()).ToLower();
		const TCHAR* Judgements[] = { TEXT("threshold"), TEXT("baseline"), TEXT("budget"), TEXT("expected"), TEXT("limit"), TEXT("target") };
		for (const TCHAR* Judgement : Judgements)
		{
			TestFalse(*FString::Printf(TEXT("the record does not judge: '%s'"), Judgement), Json.Contains(Judgement));
		}
	}

	{
		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::Wipe, 0.5, TEXT("assets"));

		TestEqual(TEXT("nothing is written when nowhere is named"),
		          WriteRecord(Timeline, FRecordContext(), FString()), FString());
	}

	{
		const FString Directory = ScratchDirectory();

		FTimeline Timeline;
		Timeline.Start();
		Timeline.Record(Phase::IntegrityPrecheck, 0.6);

		FRecordContext Context;
		Context.Bundle = TEXT("LiveImportBundle.zip");
		Context.bSucceeded = true;

		const FString Written = WriteRecord(Timeline, Context, Directory);
		TestTrue(TEXT("it reports where it wrote"), !Written.IsEmpty());
		if (!Written.IsEmpty())
		{
			TestTrue(TEXT("the file is there"), FPaths::FileExists(Written));

			FString Json;
			TestTrue(TEXT("the file reads back"), FFileHelper::LoadFileToString(Json, *Written));
			const TSharedPtr<FJsonObject> Record = ParseRecord(Json);
			TestNotNull(TEXT("what was written parses"), Record.Get());
			if (Record.IsValid())
			{
				TestEqual(TEXT("it is the same record"), Record->GetStringField(TEXT("bundle")), FString(TEXT("LiveImportBundle.zip")));
				TestEqual(TEXT("with its one phase"), Record->GetArrayField(TEXT("phases")).Num(), 1);
			}
		}

		const FString Second = WriteRecord(Timeline, Context, Directory);
		TestTrue(TEXT("the second import reports where it wrote"), !Second.IsEmpty());
		TestTrue(TEXT("and it did not overwrite the first"), Second != Written);

		TArray<FString> Found;
		IFileManager::Get().FindFiles(Found, *FPaths::Combine(Directory, TEXT("*.json")), true, false);
		TestEqual(TEXT("two records on disk"), Found.Num(), 2);

		IFileManager::Get().DeleteDirectory(*Directory, false, true);
	}

	return true;
}

#endif
