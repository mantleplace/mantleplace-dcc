// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportProvenance.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportProvenanceTest,
    "MantlePlace.Import.Provenance",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportProvenanceTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportProvenance;

	const FString Ours = TEXT("ord-11112222-3333");
	const FString Theirs = TEXT("ord-11112222-9999");

	auto RecordFor = [](const FString& Identity, int32 Scheme)
	{
		FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = Scheme;
		Record.ContentRoot = TEXT("/Game/MantlePlace");
		return Record;
	};

	{
		TestTrue(TEXT("nothing there at all"),
			Classify(false, false, FRecord(), Ours)
				== EVerdict::NoPriorImport);

		TestTrue(TEXT("a record with no content is not a prior import"),
			Classify(false, true, RecordFor(Ours, CurrentSchemeVersion), Ours)
				== EVerdict::NoPriorImport);

		TestTrue(TEXT("our folder, our identity"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion), Ours)
				== EVerdict::SameIdentity);

		TestTrue(TEXT("an older known scheme is still ours"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion - 1), Ours)
				== EVerdict::SameIdentity);
	}

	{
		TestTrue(TEXT("a colliding identity is refused, not deleted"),
			Classify(true, true, RecordFor(Theirs, CurrentSchemeVersion), Ours)
				== EVerdict::DifferentIdentity);

		TestTrue(TEXT("content with no record is legacy, and refused"),
			Classify(true, false, FRecord(), Ours) == EVerdict::LegacyUnrecorded);

		TestTrue(TEXT("a newer scheme is refused"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion + 1), Ours)
				== EVerdict::NewerScheme);

		TestTrue(TEXT("identities differing only in case are different identities"),
			Classify(true, true, RecordFor(TEXT("ORD-11112222-3333"), CurrentSchemeVersion), Ours)
				== EVerdict::DifferentIdentity);
	}

	{
		const FString Path = TEXT("/Game/MantlePlace/ord-1111");

		TestTrue(TEXT("proceeding says nothing"),
			ExplainRefusal(EVerdict::NoPriorImport, FRecord(), Path).IsEmpty());
		TestTrue(TEXT("replacing says nothing"),
			ExplainRefusal(EVerdict::SameIdentity, FRecord(), Path).IsEmpty());

		for (const EVerdict Verdict :
		     { EVerdict::DifferentIdentity, EVerdict::LegacyUnrecorded, EVerdict::NewerScheme })
		{
			const FString Message = ExplainRefusal(Verdict, RecordFor(Theirs, 99), Path);
			TestFalse(TEXT("a refusal explains itself"), Message.IsEmpty());
			TestTrue(TEXT("a refusal names the folder"), Message.Contains(Path));
		}
	}

	{
		FRecord Written = RecordFor(Ours, CurrentSchemeVersion);
		Written.JobId = TEXT("job-abcdef");

		FRecord Read;
		TestTrue(TEXT("a written record parses back"), FromJson(ToJson(Written), Read));
		TestEqual(TEXT("identity survives"), Read.Identity, Written.Identity);
		TestEqual(TEXT("scheme survives"), Read.SchemeVersion, Written.SchemeVersion);
		TestEqual(TEXT("content root survives"), Read.ContentRoot, Written.ContentRoot);
		TestEqual(TEXT("job id survives"), Read.JobId, Written.JobId);

		TestFalse(TEXT("a record with no extent reads as having none"), Read.bHasExtent);
		FRecord WithExtent = Written;
		WithExtent.bHasExtent = true;
		WithExtent.Extent.West = -97.75;
		WithExtent.Extent.South = 30.25;
		WithExtent.Extent.East = -97.73;
		WithExtent.Extent.North = 30.27;
		FRecord ReadExtent;
		TestTrue(TEXT("a record with an extent parses back"), FromJson(ToJson(WithExtent), ReadExtent));
		TestTrue(TEXT("the extent survives"), ReadExtent.bHasExtent);
		TestEqual(TEXT("west survives"), ReadExtent.Extent.West, -97.75);
		TestEqual(TEXT("south survives"), ReadExtent.Extent.South, 30.25);
		TestEqual(TEXT("east survives"), ReadExtent.Extent.East, -97.73);
		TestEqual(TEXT("north survives"), ReadExtent.Extent.North, 30.27);

		FRecord BadExtent;
		TestTrue(TEXT("an inverted extent leaves the record readable"),
			FromJson(TEXT("{\"identity\":\"ord-11112222-3333\",\"bbox\":{\"west\":-97.73,\"south\":30.25,\"east\":-97.75,\"north\":30.27}}"), BadExtent));
		TestFalse(TEXT("and reads as no extent"), BadExtent.bHasExtent);
		TestTrue(TEXT("an incomplete extent leaves the record readable"),
			FromJson(TEXT("{\"identity\":\"ord-11112222-3333\",\"bbox\":{\"west\":-97.75}}"), BadExtent));
		TestFalse(TEXT("and reads as no extent either"), BadExtent.bHasExtent);

		TestTrue(TEXT("the full identity is stored"), Read.Identity.Len() > 8);

		FRecord Ignored;
		TestFalse(TEXT("malformed json is not a record"), FromJson(TEXT("not json"), Ignored));
		TestFalse(TEXT("an empty document is not a record"), FromJson(TEXT("{}"), Ignored));
		TestFalse(TEXT("a record with no identity is not a record"),
			FromJson(TEXT("{\"scheme_version\":1}"), Ignored));
		TestFalse(TEXT("a record with an empty identity is not a record"),
			FromJson(TEXT("{\"identity\":\"\",\"scheme_version\":1}"), Ignored));

		FRecord Old;
		TestTrue(TEXT("a record with no scheme field still parses"),
			FromJson(TEXT("{\"identity\":\"ord-11112222-3333\"}"), Old));
		TestEqual(TEXT("a missing scheme reads as zero"), Old.SchemeVersion, 0);
		TestTrue(TEXT("and is treated as ours"),
			Classify(true, true, Old, Ours) == EVerdict::SameIdentity);
	}

	{
		TestEqual(TEXT("two colliding identities share a record path"),
			RecordPath(Ours), RecordPath(Theirs));
		TestTrue(TEXT("the record is not inside the content tree"),
			!RecordPath(Ours).StartsWith(TEXT("/Game")));
		TestTrue(TEXT("the record is a json file"), RecordPath(Ours).EndsWith(TEXT(".json")));
	}

	return true;
}

#endif
