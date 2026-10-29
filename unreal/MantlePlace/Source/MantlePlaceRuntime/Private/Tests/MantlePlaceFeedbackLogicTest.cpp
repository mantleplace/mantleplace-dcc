// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceFeedbackLogic.h"

// HPS-56: every host's Send Feedback opens the web Feedback form at
//   https://mantle.place/feedback?source=dcc-<host>&v=<plugin version>&hv=<host version>[&order=<order id>][&rating=up|down]
// and the pure builder is the one place that decides what goes in the link. Each parameter's rule
// is asserted here: `v` / `hv` are version strings only and a malformed one is LEFT OUT (never sent
// raw), `order` rides only on a bundle-scoped action and only as a UUID, `rating` rides only on the
// post-import prompt and only as exactly `up` or `down`, and nothing else is ever emitted. The
// once-per-bundle memory behind that prompt ("Did this import come in right?") is asserted here too.

namespace
{
const TCHAR* const SampleOrderId = TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d");

/** The parameter names in a built link, in order. */
TArray<FString> ParamNames(const FString& Url)
{
	TArray<FString> Names;
	FString Left;
	FString Query;
	if (!Url.Split(TEXT("?"), &Left, &Query))
	{
		return Names;
	}
	TArray<FString> Pairs;
	Query.ParseIntoArray(Pairs, TEXT("&"), /*bCullEmpty*/ true);
	for (const FString& Pair : Pairs)
	{
		FString Name;
		FString Value;
		Pair.Split(TEXT("="), &Name, &Value);
		Names.Add(Name);
	}
	return Names;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceFeedbackLogicTest,
	"MantlePlace.Feedback.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceFeedbackLogicTest::RunTest(const FString& Parameters)
{
	const FString Base = TEXT("https://mantle.place");

	// The general action: no order.
	TestEqual(TEXT("general link"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8")));

	// The bundle-scoped action: the same link plus &order=<id>.
	TestEqual(TEXT("bundle-scoped link"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8&order=0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d")));

	// A configured base: trailing slash and whitespace are normalised, a local dev host is honoured.
	TestEqual(TEXT("trailing slash base"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(TEXT(" http://localhost:3000/ "), TEXT("0.7.1"), TEXT("5.8"), FString()),
		FString(TEXT("http://localhost:3000/feedback?source=dcc-unreal&v=0.7.1&hv=5.8")));

	// A base that is not http(s) yields no link at all: the shim opens nothing rather than a bad URL.
	TestTrue(TEXT("empty base -> no link"), FMantlePlaceFeedbackLogic::BuildFeedbackUrl(FString(), TEXT("0.7.1"), TEXT("5.8"), FString()).IsEmpty());
	TestTrue(TEXT("non-http base -> no link"), FMantlePlaceFeedbackLogic::BuildFeedbackUrl(TEXT("javascript:alert(1)"), TEXT("0.7.1"), TEXT("5.8"), FString()).IsEmpty());

	// Version rule ^[0-9]+(\.[0-9]+){0,3}$: well-formed values are kept, malformed ones are left out.
	TestEqual(TEXT("one-part version kept"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("7"), TEXT("5"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=7&hv=5")));
	TestEqual(TEXT("four-part version kept"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("1.2.3.4"), TEXT("5.8"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=1.2.3.4&hv=5.8")));
	TestEqual(TEXT("pre-release plugin version left out"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1-beta"), TEXT("5.8"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&hv=5.8")));
	TestEqual(TEXT("empty plugin version left out"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, FString(), TEXT("5.8"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&hv=5.8")));
	TestEqual(TEXT("five-part host version left out"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8.0.1.2"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1")));
	TestEqual(TEXT("both versions malformed"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("v0.7"), TEXT("5.8 "), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal")));
	TestEqual(TEXT("trailing dot left out"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7."), TEXT(".8"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal")));
	TestEqual(TEXT("a version smuggling a parameter is left out, never sent raw"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1&email=a@b.c"), TEXT("5.8#x"), FString()),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal")));

	// order: only when given, and only as a UUID. Anything else is dropped, and the rest stands.
	TestEqual(TEXT("uppercase UUID accepted"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), TEXT("0B6C2D5E-9F31-4A7C-8E12-3D4F5A6B7C8D")),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8&order=0B6C2D5E-9F31-4A7C-8E12-3D4F5A6B7C8D")));
	const TCHAR* const NotUuids[] = {
		TEXT("not-a-uuid"),
		TEXT("0b6c2d5e9f314a7c8e123d4f5a6b7c8d"),                 // no hyphens
		TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8"),              // one short
		TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8dd"),            // one long
		TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8g"),             // non-hex
		TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d&x=1"),         // smuggled parameter
		TEXT(" 0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d"),            // leading space
	};
	for (const TCHAR* Bad : NotUuids)
	{
		TestEqual(FString::Printf(TEXT("non-UUID order dropped: %s"), Bad),
			FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), Bad),
			FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8")));
	}

	// No other parameter is ever emitted: across every case the names are a subset of the four.
	const FString Cases[] = {
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), FString()),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1&email=a@b.c"), TEXT("5.8&token=x"), TEXT("0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d&x=1"), TEXT("up&token=x")),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId, TEXT("down")),
	};
	for (const FString& Url : Cases)
	{
		for (const FString& Name : ParamNames(Url))
		{
			TestTrue(FString::Printf(TEXT("only HPS-56 parameters (%s)"), *Name),
				Name == TEXT("source") || Name == TEXT("v") || Name == TEXT("hv") || Name == TEXT("order") || Name == TEXT("rating"));
		}
	}
	TestEqual(TEXT("general link carries three parameters"), ParamNames(Cases[0]).Num(), 3);
	TestEqual(TEXT("bundle link carries four parameters"), ParamNames(Cases[1]).Num(), 4);
	TestEqual(TEXT("a bad rating, order and versions leave only the source"), ParamNames(Cases[2]).Num(), 1);
	TestEqual(TEXT("the rated link carries five parameters"), ParamNames(Cases[3]).Num(), 5);

	// rating: only the post-import prompt sends it, and only as exactly `up` or `down`.
	TestEqual(TEXT("Yes -> rating=up"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId, TEXT("up")),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8&order=0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d&rating=up")));
	TestEqual(TEXT("No -> rating=down"),
		FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId, TEXT("down")),
		FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8&order=0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d&rating=down")));
	const TCHAR* const NotRatings[] = {
		TEXT(""), TEXT(" "), TEXT("UP"), TEXT("Down"), TEXT("yes"), TEXT("no"), TEXT("1"), TEXT("5"),
		TEXT(" up"), TEXT("up "), TEXT("up\n"), TEXT("up&x=1"), TEXT("down;"), TEXT("thumbs-up"),
	};
	for (const TCHAR* Bad : NotRatings)
	{
		TestEqual(FString::Printf(TEXT("non-rating left out: %s"), Bad),
			FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), SampleOrderId, Bad),
			FString(TEXT("https://mantle.place/feedback?source=dcc-unreal&v=0.7.1&hv=5.8&order=0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d")));
	}
	TestFalse(TEXT("the general link carries no rating"), FMantlePlaceFeedbackLogic::BuildFeedbackUrl(Base, TEXT("0.7.1"), TEXT("5.8"), FString()).Contains(TEXT("rating")));
	TestTrue(TEXT("IsValidRating up"), FMantlePlaceFeedbackLogic::IsValidRating(TEXT("up")));
	TestTrue(TEXT("IsValidRating down"), FMantlePlaceFeedbackLogic::IsValidRating(TEXT("down")));
	TestFalse(TEXT("IsValidRating is case-sensitive"), FMantlePlaceFeedbackLogic::IsValidRating(TEXT("Up")));
	TestFalse(TEXT("IsValidRating empty"), FMantlePlaceFeedbackLogic::IsValidRating(FString()));

	// The post-import prompt: asked after a finished import of a bundle with an order id that is a
	// UUID, and never again for that bundle (the memory is the shim's file, the decision is here).
	TArray<FString> Asked;
	TestTrue(TEXT("a finished import of a new bundle asks"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, SampleOrderId, Asked));
	TestFalse(TEXT("a failed import asks nothing"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(false, SampleOrderId, Asked));
	TestFalse(TEXT("no order id asks nothing"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, FString(), Asked));
	TestFalse(TEXT("an order id that is not a UUID asks nothing"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, TEXT("not-a-uuid"), Asked));
	FMantlePlaceFeedbackLogic::RememberAsked(SampleOrderId, Asked);
	TestEqual(TEXT("the bundle is remembered"), Asked.Num(), 1);
	TestFalse(TEXT("a bundle already asked about is not asked about again"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, SampleOrderId, Asked));
	TestFalse(TEXT("however its id is cased"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, TEXT("0B6C2D5E-9F31-4A7C-8E12-3D4F5A6B7C8D"), Asked));
	TestTrue(TEXT("another bundle is still asked"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, TEXT("3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f7a14"), Asked));
	FMantlePlaceFeedbackLogic::RememberAsked(TEXT("0B6C2D5E-9F31-4A7C-8E12-3D4F5A6B7C8D"), Asked);
	TestEqual(TEXT("remembering a bundle twice is one entry"), Asked.Num(), 1);
	FMantlePlaceFeedbackLogic::RememberAsked(TEXT("not-a-uuid"), Asked);
	TestEqual(TEXT("an id that is not a UUID is never remembered"), Asked.Num(), 1);

	// The memory is bounded; the oldest bundle is the one forgotten.
	TArray<FString> Many;
	const FString Oldest = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
	FMantlePlaceFeedbackLogic::RememberAsked(Oldest, Many);
	for (int32 i = 0; i < FMantlePlaceFeedbackLogic::MaxAskedOrders; ++i)
	{
		FMantlePlaceFeedbackLogic::RememberAsked(FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower), Many);
	}
	TestEqual(TEXT("the memory stops at its cap"), Many.Num(), FMantlePlaceFeedbackLogic::MaxAskedOrders);
	TestTrue(TEXT("and the oldest bundle is asked about again"), FMantlePlaceFeedbackLogic::ShouldAskAboutImport(true, Oldest, Many));

	// The predicates the builder is made of.
	TestTrue(TEXT("IsValidVersion 0.7.1"), FMantlePlaceFeedbackLogic::IsValidVersion(TEXT("0.7.1")));
	TestFalse(TEXT("IsValidVersion empty"), FMantlePlaceFeedbackLogic::IsValidVersion(FString()));
	TestFalse(TEXT("IsValidVersion alpha"), FMantlePlaceFeedbackLogic::IsValidVersion(TEXT("5.8a")));
	TestTrue(TEXT("IsValidOrderId"), FMantlePlaceFeedbackLogic::IsValidOrderId(SampleOrderId));
	TestFalse(TEXT("IsValidOrderId empty"), FMantlePlaceFeedbackLogic::IsValidOrderId(FString()));

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
