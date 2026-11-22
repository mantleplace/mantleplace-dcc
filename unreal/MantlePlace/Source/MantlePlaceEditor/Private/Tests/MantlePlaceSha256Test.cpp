// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceSha256.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

namespace
{
using namespace MantlePlaceConformanceCorpus;

FString HexOfUtf8(const FString& Text)
{
	const FTCHARToUTF8 Utf8(*Text);
	const uint8* Bytes = reinterpret_cast<const uint8*>(Utf8.Get());
	return MantlePlaceSha256::HexDigest(TConstArrayView<uint8>(Bytes, Utf8.Length()));
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceSha256Test,
	"MantlePlace.Import.Sha256",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceSha256Test::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("digest"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	TSet<FString> Driven;

	if (const FCase* Case = FindCase(Cases, TEXT("digest.sha256Vectors")))
	{
		Driven.Add(Case->Id);

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Input = RowString(Row, TEXT("input"));
			TestEqual(
				FString::Printf(TEXT("[%s] sha256(\"%s\")"), *Case->Id, *Input.Left(16)),
				HexOfUtf8(Input),
				RowString(Row, TEXT("sha256")));
		}

		const TArray<TSharedPtr<FJsonObject>> Streaming = Rows(*Case, TEXT("streamingEquivalence"));
		TestTrue(Case->What(TEXT("has streamingEquivalence rows")), Streaming.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Streaming)
		{
			const FString Whole = RowString(Row, TEXT("equalsOneShotOf"));
			const TSharedPtr<FJsonObject>* Known = Vectors.FindByPredicate(
				[&Whole](const TSharedPtr<FJsonObject>& Vector)
				{ return RowString(Vector, TEXT("input")) == Whole; });

			if (Known == nullptr)
			{
				AddError(FString::Printf(
					TEXT("[%s] streamingEquivalence names \"%s\", which the vectors table has no known ")
					TEXT("answer for — without one this assertion cannot fail"),
					*Case->Id, *Whole.Left(24)));
				continue;
			}
			TestEqual(
				FString::Printf(TEXT("[%s] concatenated chunks of \"%s\""), *Case->Id, *Whole.Left(16)),
				HexOfUtf8(FString::Join(RowStrings(Row, TEXT("chunks")), TEXT(""))),
				RowString(*Known, TEXT("sha256")));
		}

		const FString Digest = HexOfUtf8(TEXT("mantleplace"));
		TestEqual(Case->What(TEXT("digest length")), Digest.Len(), 64);
		TestEqual(Case->What(TEXT("digest is lowercase")), Digest, Digest.ToLower());
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the digest group but nothing here drives it (HPS-41)"), *Missing));
	}

	return true;
}

#endif
