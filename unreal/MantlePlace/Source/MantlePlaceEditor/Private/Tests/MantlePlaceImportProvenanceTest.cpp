// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportProvenance.h"

// Classify() decides whether the importer force-deletes a directory of a user's content. Every
// branch is asserted, and the ones that REFUSE matter more than the one that proceeds: a wrong
// "proceed" destroys work that cannot be recovered from the editor, while a wrong "refuse" costs
// somebody a sentence in the import log.
//
// Pure by construction — it takes what was found rather than looking, which is what makes the
// deletion decision assertable at all without an editor, a project and two colliding bundles.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportProvenanceTest,
    "MantlePlace.Import.Provenance",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportProvenanceTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportProvenance;

	const FString Ours = TEXT("ord-11112222-3333");
	const FString Theirs = TEXT("ord-11112222-9999"); // same first eight characters, different order

	auto RecordFor = [](const FString& Identity, int32 Scheme)
	{
		FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = Scheme;
		Record.ContentRoot = TEXT("/Game/MantlePlace");
		return Record;
	};

	// --- Proceeds -------------------------------------------------------------------------------
	{
		TestTrue(TEXT("nothing there at all"),
			Classify(/*bFolderExists*/ false, /*bRecordFound*/ false, FRecord(), Ours)
				== EVerdict::NoPriorImport);

		// A stale record with no content is what a user who deleted the folder by hand leaves
		// behind. They meant the next import to be a fresh one, so it is.
		TestTrue(TEXT("a record with no content is not a prior import"),
			Classify(false, true, RecordFor(Ours, CurrentSchemeVersion), Ours)
				== EVerdict::NoPriorImport);

		// The ordinary case: re-importing the same order replaces its own content.
		TestTrue(TEXT("our folder, our identity"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion), Ours)
				== EVerdict::SameIdentity);

		// An older scheme we still understand is ours to replace.
		TestTrue(TEXT("an older known scheme is still ours"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion - 1), Ours)
				== EVerdict::SameIdentity);
	}

	// --- Refuses --------------------------------------------------------------------------------
	{
		// THE case this record exists for. Two identities sharing eight characters name one folder;
		// without the record, importing the second would force-delete the first order's content
		// with no warning and no undo.
		TestTrue(TEXT("a colliding identity is refused, not deleted"),
			Classify(true, true, RecordFor(Theirs, CurrentSchemeVersion), Ours)
				== EVerdict::DifferentIdentity);

		// Content from 0.3.0 and earlier has no record. It was keyed on a build, so this importer
		// cannot tell whether it belongs to this order.
		TestTrue(TEXT("content with no record is legacy, and refused"),
			Classify(true, false, FRecord(), Ours) == EVerdict::LegacyUnrecorded);

		// A newer plugin's layout is one this build cannot predict, so it cannot know what deleting
		// would destroy.
		TestTrue(TEXT("a newer scheme is refused"),
			Classify(true, true, RecordFor(Ours, CurrentSchemeVersion + 1), Ours)
				== EVerdict::NewerScheme);

		// Case is significant in both an order id and a hex digest. Treating two identities that
		// differ only in case as one would be inventing an equivalence nobody stated — and would
		// license a deletion on the strength of it.
		TestTrue(TEXT("identities differing only in case are different identities"),
			Classify(true, true, RecordFor(TEXT("ORD-11112222-3333"), CurrentSchemeVersion), Ours)
				== EVerdict::DifferentIdentity);
	}

	// --- What the user is told ------------------------------------------------------------------
	{
		const FString Path = TEXT("/Game/MantlePlace/ord-1111");

		TestTrue(TEXT("proceeding says nothing"),
			ExplainRefusal(EVerdict::NoPriorImport, FRecord(), Path).IsEmpty());
		TestTrue(TEXT("replacing says nothing"),
			ExplainRefusal(EVerdict::SameIdentity, FRecord(), Path).IsEmpty());

		// Every refusal names the folder, so the user can go and look at it. A refusal that does
		// not say where is a dead end.
		for (const EVerdict Verdict :
		     { EVerdict::DifferentIdentity, EVerdict::LegacyUnrecorded, EVerdict::NewerScheme })
		{
			const FString Message = ExplainRefusal(Verdict, RecordFor(Theirs, 99), Path);
			TestFalse(TEXT("a refusal explains itself"), Message.IsEmpty());
			TestTrue(TEXT("a refusal names the folder"), Message.Contains(Path));
		}
	}

	// --- The record survives a round trip -------------------------------------------------------
	{
		FRecord Written = RecordFor(Ours, CurrentSchemeVersion);
		Written.JobId = TEXT("job-abcdef");

		FRecord Read;
		TestTrue(TEXT("a written record parses back"), FromJson(ToJson(Written), Read));
		TestEqual(TEXT("identity survives"), Read.Identity, Written.Identity);
		TestEqual(TEXT("scheme survives"), Read.SchemeVersion, Written.SchemeVersion);
		TestEqual(TEXT("content root survives"), Read.ContentRoot, Written.ContentRoot);
		TestEqual(TEXT("job id survives"), Read.JobId, Written.JobId);

		// The full identity is what round-trips, not the truncation — the collision check compares
		// it, so a record that stored only eight characters could not answer the question.
		TestTrue(TEXT("the full identity is stored"), Read.Identity.Len() > 8);

		// A record that cannot answer the one question it exists for is not a record. Treated as
		// absent, which refuses rather than deletes.
		FRecord Ignored;
		TestFalse(TEXT("malformed json is not a record"), FromJson(TEXT("not json"), Ignored));
		TestFalse(TEXT("an empty document is not a record"), FromJson(TEXT("{}"), Ignored));
		TestFalse(TEXT("a record with no identity is not a record"),
			FromJson(TEXT("{\"scheme_version\":1}"), Ignored));
		TestFalse(TEXT("a record with an empty identity is not a record"),
			FromJson(TEXT("{\"identity\":\"\",\"scheme_version\":1}"), Ignored));

		// A record written by an older build that had no scheme field reads as scheme 0, which is
		// older than the current one and therefore still ours to replace.
		FRecord Old;
		TestTrue(TEXT("a record with no scheme field still parses"),
			FromJson(TEXT("{\"identity\":\"ord-11112222-3333\"}"), Old));
		TestEqual(TEXT("a missing scheme reads as zero"), Old.SchemeVersion, 0);
		TestTrue(TEXT("and is treated as ours"),
			Classify(true, true, Old, Ours) == EVerdict::SameIdentity);
	}

	// --- Where the record lives -----------------------------------------------------------------
	{
		// Keyed on the SHORT identity, because the folder is what has to be looked up from. Its
		// contents are what disambiguate.
		TestEqual(TEXT("two colliding identities share a record path"),
			RecordPath(Ours), RecordPath(Theirs));
		TestTrue(TEXT("the record is not inside the content tree"),
			!RecordPath(Ours).StartsWith(TEXT("/Game")));
		TestTrue(TEXT("the record is a json file"), RecordPath(Ours).EndsWith(TEXT(".json")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
