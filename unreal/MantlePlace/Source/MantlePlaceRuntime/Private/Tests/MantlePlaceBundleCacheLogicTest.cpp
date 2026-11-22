// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceBundleCacheLogic.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"

namespace
{
using namespace MantlePlaceConformanceCorpus;

FString ReasonName(EMantlePlaceCacheInvalidReason Reason)
{
	switch (Reason)
	{
		case EMantlePlaceCacheInvalidReason::Missing:        return TEXT("Missing");
		case EMantlePlaceCacheInvalidReason::SizeMismatch:   return TEXT("SizeMismatch");
		case EMantlePlaceCacheInvalidReason::Sha256Mismatch: return TEXT("Sha256Mismatch");
		case EMantlePlaceCacheInvalidReason::ManifestTooOld: return TEXT("ManifestTooOld");
		default:                                             return TEXT("None");
	}
}

FString CacheStateName(EMantlePlaceCacheState State)
{
	switch (State)
	{
		case EMantlePlaceCacheState::CachedValid: return TEXT("CachedValid");
		case EMantlePlaceCacheState::CachedStale: return TEXT("CachedStale");
		default:                                  return TEXT("NotCached");
	}
}

FString Sha256OfUtf8(const FString& Text)
{
	const FTCHARToUTF8 Utf8(*Text);
	return FMantlePlaceSha256::HexDigest(
		TConstArrayView<uint8>(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length()));
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceBundleCacheLogicTest,
	"MantlePlace.BundleCache.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceBundleCacheLogicTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

	using FLogic = FMantlePlaceBundleCacheLogic;
	const FString Root = TEXT("C:/Cache");

	{
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
					Sha256OfUtf8(Input),
					RowString(Row, TEXT("sha256")));
			}

			const TArray<TSharedPtr<FJsonObject>> Streaming = Rows(*Case, TEXT("streamingEquivalence"));
			TestTrue(Case->What(TEXT("has streamingEquivalence rows")), Streaming.Num() > 0);
			for (const TSharedPtr<FJsonObject>& Row : Streaming)
			{
				const TArray<FString> Chunks = RowStrings(Row, TEXT("chunks"));
				const FString Whole = RowString(Row, TEXT("equalsOneShotOf"));

				FMantlePlaceSha256 Streamed;
				for (const FString& Chunk : Chunks)
				{
					const FTCHARToUTF8 Utf8(*Chunk);
					Streamed.Update(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
				}
				TestEqual(
					FString::Printf(TEXT("[%s] streamed %d chunks == one-shot of \"%s\""),
						*Case->Id, Chunks.Num(), *Whole.Left(16)),
					Streamed.Final(),
					Sha256OfUtf8(Whole));
			}

			const FString Comparison = RowString(Case->PayloadObject, TEXT("comparison"));
			TestTrue(Case->What(TEXT("states a comparison rule")), !Comparison.IsEmpty());
			TestTrue(Case->What(TEXT("comparison ignores case")),
				FLogic::Sha256Equal(TEXT("ABCDEF"), TEXT("abcdef")));
			TestTrue(Case->What(TEXT("comparison trims both ends")),
				FLogic::Sha256Equal(TEXT("  abcd  "), TEXT("abcd")));
			TestFalse(Case->What(TEXT("different digests are not equal")),
				FLogic::Sha256Equal(TEXT("abcd"), TEXT("abce")));

			TestEqual(Case->What(TEXT("sha256 of a null/zero-length buffer")),
				FMantlePlaceSha256::HexDigest(TConstArrayView<uint8>()), Sha256OfUtf8(FString()));
		}

		for (const FString& Missing : UndrivenCases(Cases, Driven))
		{
			AddError(FString::Printf(
				TEXT("corpus case '%s' is in the digest group but nothing here drives it (HPS-41)"),
				*Missing));
		}
	}

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("cache"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	TSet<FString> Driven;

	if (const FCase* Case = FindCase(Cases, TEXT("cache.keySanitisation")))
	{
		Driven.Add(Case->Id);

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Sanitised = RowString(Row, TEXT("sanitisedDir"));
			const bool bLossy = RowBool(Row, TEXT("lossy"));
			const bool bSuffixed = RowBool(Row, TEXT("suffixed"));

			TArray<FString> Ids = RowStrings(Row, TEXT("orderIdPair"));
			if (Ids.Num() == 0)
			{
				FString Single;
				Row->TryGetStringField(TEXT("orderId"), Single);
				Ids.Add(Single);
			}

			for (const FString& OrderId : Ids)
			{
				const FString Where = FString::Printf(TEXT("[%s] \"%s\""), *Case->Id, *OrderId);
				TestEqual(Where + TEXT(" sanitisedDir"), FLogic::SanitizeKeySegment(OrderId), Sanitised);

				const FString Dir = FLogic::DeriveBundleDir(Root, OrderId);
				const FString Unsuffixed = Root / Sanitised;
				if (bSuffixed)
				{
					const FString Expected = Unsuffixed + TEXT("_") + Sha256OfUtf8(OrderId).Left(8);
					TestEqual(Where + TEXT(" lossy dir carries the raw-id hash suffix"), Dir, Expected);
				}
				else
				{
					TestEqual(Where + TEXT(" lossless dir is unsuffixed"), Dir, Unsuffixed);
				}
				const FString Segment = Dir.RightChop(Root.Len() + 1);
				TestTrue(Where + TEXT(" stays under the cache root"), Dir.StartsWith(Root + TEXT("/")));
				TestFalse(Where + TEXT(" segment has no forward slash"), Segment.Contains(TEXT("/")));
				TestFalse(Where + TEXT(" segment has no backslash"), Segment.Contains(TEXT("\\")));
				TestTrue(Where + TEXT(" lossy flag matches what the sanitiser actually did"),
					bLossy == (FLogic::SanitizeKeySegment(OrderId) != OrderId));
			}

			if (RowBool(Row, TEXT("mustDiffer")) && Ids.Num() == 2)
			{
				TestNotEqual(Case->What(TEXT("colliding ids never share a cache dir")),
					FLogic::DeriveBundleDir(Root, Ids[0]), FLogic::DeriveBundleDir(Root, Ids[1]));
			}
		}

		const TSharedPtr<FJsonObject>* FileNames = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("fileNames"), FileNames)
			&& FileNames != nullptr)
		{
			const FString Uuid = TEXT("3f285101-0310-425b-b06b-bdb73b025b6a");
			const FString Dir = FLogic::DeriveBundleDir(Root, Uuid);
			TestEqual(Case->What(TEXT("final file name")),
				FLogic::DeriveBundlePath(Root, Uuid), Dir / RowString(*FileNames, TEXT("final")));
			TestEqual(Case->What(TEXT("partial file name")),
				FLogic::DerivePartPath(Root, Uuid), Dir / RowString(*FileNames, TEXT("partial")));
			TestEqual(Case->What(TEXT("sidecar file name")),
				FLogic::DeriveMetaPath(Root, Uuid), Dir / RowString(*FileNames, TEXT("sidecar")));
		}
		else
		{
			AddError(Case->What(TEXT("fileNames table missing")));
		}
	}

	{
		const FString Astral = TEXT("\U0001D7CE");
		TestEqual(TEXT("[HPS-30] an astral alphanumeric is BMP-bounded to one underscore"),
			FLogic::SanitizeKeySegment(Astral), FString(TEXT("_")));
		TestEqual(TEXT("[HPS-30] an astral non-alphanumeric is one underscore"),
			FLogic::SanitizeKeySegment(TEXT("a\U0001F600b")), FString(TEXT("a_b")));

		FString LoneHigh;
		LoneHigh.AppendChar(static_cast<TCHAR>(0xD835));
		TestEqual(TEXT("[HPS-30] an unpaired surrogate is one underscore"),
			FLogic::SanitizeKeySegment(LoneHigh), FString(TEXT("_")));

		TestEqual(TEXT("[HPS-30] the astral dir carries the raw-id hash suffix"),
			FLogic::DeriveBundleDir(Root, Astral),
			(Root / TEXT("_")) + TEXT("_") + Sha256OfUtf8(Astral).Left(8));
	}

	if (const FCase* Case = FindCase(Cases, TEXT("cache.validityTruthTable")))
	{
		Driven.Add(Case->Id);

		FString MinVersion;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringField(TEXT("minSupportedManifestVersion"), MinVersion);
		}
		TestEqual(Case->What(TEXT("corpus floor == this host's MinSupportedManifestVersion")),
			MinVersion, MantlePlaceMinSupportedManifestVersion);

		const TArray<TSharedPtr<FJsonObject>> TruthRows = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has truth-table rows")), TruthRows.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : TruthRows)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const FString Where = FString::Printf(TEXT("[%s] \"%s\""), *Case->Id, *Name);

			FString ExpectedSha;
			const bool bHasExpectedSha = Row->TryGetStringField(TEXT("expectedSha256"), ExpectedSha);
			double ExpectedSize = 0.0;
			const bool bHasExpectedSize = Row->TryGetNumberField(TEXT("expectedSizeBytes"), ExpectedSize);
			FString Version;
			bool bHasVersion = Row->TryGetStringField(TEXT("manifestVersion"), Version);
			if (!bHasVersion)
			{
				double NumericVersion = 0.0;
				bHasVersion = Row->TryGetNumberField(TEXT("manifestVersion"), NumericVersion);
				if (bHasVersion)
				{
					Version = FString::FromInt(static_cast<int32>(NumericVersion));
				}
			}

			const FMantlePlaceCacheValidity Validity = FLogic::DecideValidity(
				RowBool(Row, TEXT("fileExists")),
				static_cast<int64>(RowNumber(Row, TEXT("onDiskSizeBytes"))),
				RowString(Row, TEXT("computedSha256")),
				bHasExpectedSha,
				ExpectedSha,
				bHasExpectedSize,
				static_cast<int64>(ExpectedSize),
				bHasVersion,
				Version,
				MinVersion);

			TestTrue(Where + TEXT(" valid"), Validity.bValid == RowBool(Row, TEXT("valid")));
			TestEqual(Where + TEXT(" reason"), ReasonName(Validity.Reason), RowString(Row, TEXT("reason")));
			TestTrue(Where + TEXT(" integrityChecked"),
				Validity.bIntegrityChecked == RowBool(Row, TEXT("integrityChecked")));
		}

		const FMantlePlaceCacheValidity Valid = FLogic::DecideValidity(
			true, 1024, TEXT("ab"), true, TEXT("ab"), true, 1024, true, MinVersion, MinVersion);
		const FMantlePlaceCacheValidity Stale = FLogic::DecideValidity(
			true, 1024, TEXT("ab"), true, TEXT("cd"), true, 1024, true, MinVersion, MinVersion);
		const FMantlePlaceCacheValidity Absent = FLogic::DecideValidity(
			false, 0, TEXT(""), false, TEXT(""), false, 0, false, FString(), MinVersion);

		const TArray<TSharedPtr<FJsonObject>> StateRows = Rows(*Case, TEXT("cacheState"));
		TestTrue(Case->What(TEXT("has cacheState rows")), StateRows.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : StateRows)
		{
			const bool bExists = RowBool(Row, TEXT("fileExists"));
			const FMantlePlaceCacheValidity& Which =
				!bExists ? Absent : (RowBool(Row, TEXT("valid")) ? Valid : Stale);
			TestEqual(
				FString::Printf(TEXT("[%s] cacheState(exists=%s, valid=%s)"), *Case->Id,
					bExists ? TEXT("true") : TEXT("false"),
					RowBool(Row, TEXT("valid")) ? TEXT("true") : TEXT("false")),
				CacheStateName(FLogic::DeriveCacheState(bExists, Which)),
				RowString(Row, TEXT("state")));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the cache group but nothing here drives it (HPS-41)"), *Missing));
	}

	{
		FDateTime Expiry;
		TestTrue(TEXT("ParseExpiry parses ISO-8601"),
			FLogic::ParseExpiry(TEXT("2026-06-20T00:00:00.000Z"), Expiry));
		TestFalse(TEXT("ParseExpiry rejects empty"), FLogic::ParseExpiry(TEXT(""), Expiry));

		const FDateTime DayBefore(2026, 6, 19, 0, 0, 0);
		const FDateTime AtExpiry(2026, 6, 20, 0, 0, 0);
		const FDateTime JustInsideSkew(2026, 6, 19, 23, 59, 30);
		TestFalse(TEXT("Not expired a day before"), FLogic::IsExpired(DayBefore, Expiry));
		TestTrue(TEXT("Expired at the expiry instant"), FLogic::IsExpired(AtExpiry, Expiry));
		TestTrue(TEXT("Expired within the skew window"), FLogic::IsExpired(JustInsideSkew, Expiry));
	}

	{
		FMantlePlaceCachedBundle Meta;
		Meta.OrderId = TEXT("f3c1");
		Meta.LocalPath = TEXT("C:/Cache/f3c1/bundle.zip");
		Meta.Sha256 = TEXT("ab12");
		Meta.SizeBytes = 134217728;
		Meta.ManifestVersion = MantlePlaceMinSupportedManifestVersion;
		Meta.DownloadedAtUtc = TEXT("2026-06-19T12:00:00.000Z");
		Meta.Format = TEXT("glb");

		const FString Json = FLogic::SerializeMeta(Meta);
		FMantlePlaceCachedBundle Out;
		FString Error;
		TestTrue(TEXT("Meta round-trips"), FLogic::ParseMeta(Json, Out, Error));
		TestEqual(TEXT("Meta orderId"), Out.OrderId, Meta.OrderId);
		TestEqual(TEXT("Meta localPath"), Out.LocalPath, Meta.LocalPath);
		TestEqual(TEXT("Meta sha256"), Out.Sha256, Meta.Sha256);
		TestTrue(TEXT("Meta sizeBytes"), Out.SizeBytes == Meta.SizeBytes);
		TestEqual(TEXT("Meta manifestVersion"), Out.ManifestVersion, Meta.ManifestVersion);
		TestEqual(TEXT("Meta format"), Out.Format, Meta.Format);

		FMantlePlaceCachedBundle Bad;
		TestFalse(TEXT("Malformed JSON fails closed"), FLogic::ParseMeta(TEXT("not json"), Bad, Error));
		TestFalse(TEXT("Missing orderId fails closed"), FLogic::ParseMeta(TEXT("{\"format\":\"glb\"}"), Bad, Error));
	}

	{
		const FMantlePlaceCacheExpectation HeldView = FLogic::ExpectationForHeld(
			true, TEXT("beef"), 4194304, true, TEXT("cafe"), true, 480000000);
		TestTrue(TEXT("A cached view is held against its recorded digest"), HeldView.bHasSha256 && HeldView.Sha256 == TEXT("beef"));
		TestTrue(TEXT("A cached view is held against its recorded size"), HeldView.bHasSizeBytes && HeldView.SizeBytes == 4194304);

		const FMantlePlaceCacheExpectation HeldWhole = FLogic::ExpectationForHeld(
			false, TEXT("beef"), 4194304, true, TEXT("cafe"), true, 480000000);
		TestTrue(TEXT("A cached whole archive is held against the listing"),
			HeldWhole.Sha256 == TEXT("cafe") && HeldWhole.SizeBytes == 480000000);

		const FMantlePlaceCacheExpectation HeldNoRecord = FLogic::ExpectationForHeld(
			true, TEXT(""), 0, true, TEXT("cafe"), true, 480000000);
		TestFalse(TEXT("A view with no recorded digest is unverified, not held to the listing"), HeldNoRecord.bHasSha256);
		TestFalse(TEXT("A view with no recorded size is unverified, not held to the listing"), HeldNoRecord.bHasSizeBytes);

		const FMantlePlaceCacheValidity Valid = FLogic::DecideValidity(
			true, 4194304, TEXT("beef"), HeldView.bHasSha256, HeldView.Sha256, HeldView.bHasSizeBytes, HeldView.SizeBytes,
			false, TEXT(""));
		TestTrue(TEXT("A view is valid against its own record"), Valid.bValid && Valid.bIntegrityChecked);
		const FMantlePlaceCacheValidity AgainstListing = FLogic::DecideValidity(
			true, 4194304, TEXT("beef"), true, TEXT("cafe"), true, 480000000, false, TEXT(""));
		TestFalse(TEXT("The same bytes fail the listing's whole-archive facts"), AgainstListing.bValid);
		const FMantlePlaceCacheValidity Truncated = FLogic::DecideValidity(
			true, 1000, TEXT("beef"), HeldView.bHasSha256, HeldView.Sha256, HeldView.bHasSizeBytes, HeldView.SizeBytes,
			false, TEXT(""));
		TestFalse(TEXT("A truncated view fails its own record"), Truncated.bValid);

		TestTrue(TEXT("A view is re-cut when the listing's digest moved"),
			FLogic::IsViewRecut(true, TEXT("aaaa"), true, TEXT("bbbb")));
		TestFalse(TEXT("The same listing digest is not a re-cut"),
			FLogic::IsViewRecut(true, TEXT("aaaa"), true, TEXT("AAAA")));
		TestFalse(TEXT("No recorded listing digest (an older sidecar) is not a re-cut"),
			FLogic::IsViewRecut(true, TEXT(""), true, TEXT("bbbb")));
		TestFalse(TEXT("A listing with no digest is not a re-cut"),
			FLogic::IsViewRecut(true, TEXT("aaaa"), false, TEXT("")));
		TestFalse(TEXT("A whole archive is held against the listing, not this"),
			FLogic::IsViewRecut(false, TEXT("aaaa"), true, TEXT("bbbb")));

		const FMantlePlaceCacheExpectation DownView = FLogic::ExpectationForDownload(
			true, true, 4194304, true, TEXT("cafe"), true, 480000000);
		TestFalse(TEXT("A downloading view has no listing digest to match"), DownView.bHasSha256);
		TestTrue(TEXT("A downloading view is checked for its stated length"), DownView.bHasSizeBytes && DownView.SizeBytes == 4194304);
		const FMantlePlaceCacheExpectation DownViewNoSize = FLogic::ExpectationForDownload(
			true, false, 0, true, TEXT("cafe"), true, 480000000);
		TestFalse(TEXT("A view with no stated length is not held to the whole archive's"), DownViewNoSize.bHasSizeBytes);
		const FMantlePlaceCacheExpectation DownWhole = FLogic::ExpectationForDownload(
			false, false, 0, true, TEXT("cafe"), true, 480000000);
		TestTrue(TEXT("A downloading whole archive is checked against the listing"),
			DownWhole.bHasSha256 && DownWhole.Sha256 == TEXT("cafe") && DownWhole.SizeBytes == 480000000);

		FMantlePlaceCachedBundle ViewMeta;
		ViewMeta.OrderId = TEXT("f3c1");
		ViewMeta.bHoldsView = true;
		FMantlePlaceCachedBundle ViewBack;
		FString MetaError;
		TestTrue(TEXT("A view's sidecar round-trips"), FLogic::ParseMeta(FLogic::SerializeMeta(ViewMeta), ViewBack, MetaError));
		TestTrue(TEXT("The sidecar keeps holdsView"), ViewBack.bHoldsView);
		FMantlePlaceCachedBundle OldBack;
		TestTrue(TEXT("A pre-view sidecar parses"), FLogic::ParseMeta(TEXT("{\"orderId\":\"f3c1\",\"sha256\":\"ab\"}"), OldBack, MetaError));
		TestFalse(TEXT("A pre-view sidecar holds a whole archive"), OldBack.bHoldsView);
	}

	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceBundleCacheLevelTest,
	"MantlePlace.BundleCache.Level",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceBundleCacheLevelTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceBundleCacheLogic;
	using E = EMantlePlaceCacheState;

	TestEqual(TEXT("The same level reuses the cached download"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, TEXT("Med"), TEXT("Med"))), FString(TEXT("CachedValid")));
	TestEqual(TEXT("A different level is downloaded afresh"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, TEXT("Med"), TEXT("Max"))), FString(TEXT("CachedStale")));
	TestEqual(TEXT("A level is spelled exactly"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, TEXT("MED"), TEXT("Med"))), FString(TEXT("CachedStale")));

	TestEqual(TEXT("A 2.x slot serves a 2.x download"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, FString(), FString())), FString(TEXT("CachedValid")));
	TestEqual(TEXT("A 2.x slot does not serve a 3.0 level"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, FString(), TEXT("Max"))), FString(TEXT("CachedStale")));
	TestEqual(TEXT("A 3.0 slot does not serve a 2.x download"),
		CacheStateName(FLogic::StateAtLevel(E::CachedValid, TEXT("Min"), FString())), FString(TEXT("CachedStale")));

	TestEqual(TEXT("Nothing cached stays nothing cached"),
		CacheStateName(FLogic::StateAtLevel(E::NotCached, FString(), TEXT("Med"))), FString(TEXT("NotCached")));
	TestEqual(TEXT("A stale slot stays stale at its own level"),
		CacheStateName(FLogic::StateAtLevel(E::CachedStale, TEXT("Med"), TEXT("Med"))), FString(TEXT("CachedStale")));

	FMantlePlaceCachedBundle Meta;
	Meta.OrderId = TEXT("order-3");
	Meta.bHoldsView = true;
	Meta.Level = TEXT("Med");
	FMantlePlaceCachedBundle Back;
	FString Error;
	TestTrue(TEXT("A 3.0 sidecar round-trips"), FLogic::ParseMeta(FLogic::SerializeMeta(Meta), Back, Error));
	TestEqual(TEXT("The sidecar keeps the level"), Back.Level, FString(TEXT("Med")));

	FMantlePlaceCachedBundle TwoX;
	TwoX.OrderId = TEXT("order-2");
	TestFalse(TEXT("A 2.x sidecar writes no level"), FLogic::SerializeMeta(TwoX).Contains(TEXT("\"level\"")));
	FMantlePlaceCachedBundle OldBack;
	TestTrue(TEXT("A sidecar from before 3.0 parses"), FLogic::ParseMeta(TEXT("{\"orderId\":\"order-2\",\"sha256\":\"ab\"}"), OldBack, Error));
	TestTrue(TEXT("And holds no level"), OldBack.Level.IsEmpty());
	return true;
}

#endif
