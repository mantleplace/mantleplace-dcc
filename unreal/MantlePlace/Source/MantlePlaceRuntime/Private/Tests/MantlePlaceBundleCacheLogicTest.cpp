// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceBundleCacheLogic.h"
#include "MantlePlaceBundleCacheTypes.h"
#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"

// The cache-validity truth table, the cache-key sanitisation vectors and the SHA-256 known answers
// come from the shared conformance corpus (HPS-40): tools/manifest-conformance/corpus/{cache,digest}/.
//
// The validity table is the one every host gets subtly wrong. `null` sha256 means UNKNOWN, not
// absent and not zero — a host that treats it as a mismatch makes every legacy bundle un-openable,
// and a host that reports bIntegrityChecked=true for it claims a verification it never did.

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

/** Lowercase-hex SHA-256 of a string's UTF-8 bytes, through the logic layer under test. */
FString Sha256OfUtf8(const FString& Text)
{
	const FTCHARToUTF8 Utf8(*Text);
	return FMantlePlaceBundleCacheLogic::Sha256Hex(
		reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceBundleCacheLogicTest,
	"MantlePlace.BundleCache.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceBundleCacheLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceBundleCacheLogic;
	const FString Root = TEXT("C:/Cache");

	// ========================================================================================
	// digest group — the FIPS 180-4 known answers, plus streaming == one-shot
	// ========================================================================================
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

			// The streaming path must agree with the one-shot path across a chunk boundary that is
			// NOT block-aligned — the classic place a hand-rolled SHA-256 diverges.
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

			// The corpus states the comparison rule as prose ("trim both ends, compare
			// case-insensitively"); it is applied here rather than parsed, so the assertion is
			// pinned to the sentence a second host author reads.
			const FString Comparison = RowString(Case->PayloadObject, TEXT("comparison"));
			TestTrue(Case->What(TEXT("states a comparison rule")), !Comparison.IsEmpty());
			TestTrue(Case->What(TEXT("comparison ignores case")),
				FLogic::Sha256Equal(TEXT("ABCDEF"), TEXT("abcdef")));
			TestTrue(Case->What(TEXT("comparison trims both ends")),
				FLogic::Sha256Equal(TEXT("  abcd  "), TEXT("abcd")));
			TestFalse(Case->What(TEXT("different digests are not equal")),
				FLogic::Sha256Equal(TEXT("abcd"), TEXT("abce")));

			// A zero-length buffer arrives as a null pointer from the shim's empty-file path; the
			// corpus states sha256("") but not how the caller passes "nothing".
			TestEqual(Case->What(TEXT("sha256 of a null/zero-length buffer")),
				FLogic::Sha256Hex(nullptr, 0), Sha256OfUtf8(FString()));
		}

		for (const FString& Missing : UndrivenCases(Cases, Driven))
		{
			AddError(FString::Printf(
				TEXT("corpus case '%s' is in the digest group but nothing here drives it (HPS-41)"),
				*Missing));
		}
	}

	// ========================================================================================
	// cache group — key sanitisation and the validity truth table
	// ========================================================================================
	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("cache"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	TSet<FString> Driven;

	// --- cache.keySanitisation --------------------------------------------------------------
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

			// A row is either one id or a pair that must collide under sanitisation but not on disk.
			TArray<FString> Ids = RowStrings(Row, TEXT("orderIdPair"));
			if (Ids.Num() == 0)
			{
				FString Single;
				Row->TryGetStringField(TEXT("orderId"), Single); // "" is a real vector, not an absence
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
					// '_' + the first 8 hex chars of sha256(utf8(rawOrderId)) — recomputed here, so a
					// host that invents a different suffix scheme diverges immediately.
					const FString Expected = Unsuffixed + TEXT("_") + Sha256OfUtf8(OrderId).Left(8);
					TestEqual(Where + TEXT(" lossy dir carries the raw-id hash suffix"), Dir, Expected);
				}
				else
				{
					TestEqual(Where + TEXT(" lossless dir is unsuffixed"), Dir, Unsuffixed);
				}
				// ".." surviving as literal characters is fine — what must not survive is a path
				// SEPARATOR, which is what would turn the segment into an escape from the root.
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

		// The three file names the corpus fixes, so a second host lands on the same layout and a
		// half-written download is never mistaken for a complete one (HPS-26).
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

	// --- HPS-30, hand-written: the surrogate rows ---------------------------------------------
	// The two astral rows are corpus vectors above and are asserted again here against literal
	// expected strings. Both failing together is a real regression; only the vector-driven one
	// failing localises it to the reader instead of to the sanitiser. The unpaired-surrogate row
	// is host-local because a lone surrogate has no portable spelling in a JSON corpus file.
	{
		// One code point in, exactly ONE underscore out. The pre-fix walk consumed UTF-16 code
		// UNITS and emitted two, so this host derived a different cache directory than Revit for
		// the same order and neither could notice.
		const FString Astral = TEXT("\U0001D7CE"); // MATHEMATICAL BOLD DIGIT ZERO, category Nd
		TestEqual(TEXT("[HPS-30] an astral alphanumeric is BMP-bounded to one underscore"),
			FLogic::SanitizeKeySegment(Astral), FString(TEXT("_")));
		TestEqual(TEXT("[HPS-30] an astral non-alphanumeric is one underscore"),
			FLogic::SanitizeKeySegment(TEXT("a\U0001F600b")), FString(TEXT("a_b")));

		// An unpaired surrogate is not a code point at all, so it is never alphanumeric — and the
		// decoder must not run off the end of the string looking for the half that is missing.
		// Built a code unit at a time because a lone surrogate is not a legal string literal.
		FString LoneHigh;
		LoneHigh.AppendChar(static_cast<TCHAR>(0xD835));
		TestEqual(TEXT("[HPS-30] an unpaired surrogate is one underscore"),
			FLogic::SanitizeKeySegment(LoneHigh), FString(TEXT("_")));

		// Lossy, so suffixed — over the RAW id, which is what keeps two distinct astral ids in
		// distinct directories under a rule that maps both their stems to the same underscore.
		TestEqual(TEXT("[HPS-30] the astral dir carries the raw-id hash suffix"),
			FLogic::DeriveBundleDir(Root, Astral),
			(Root / TEXT("_")) + TEXT("_") + Sha256OfUtf8(Astral).Left(8));
	}

	// --- cache.validityTruthTable -----------------------------------------------------------
	if (const FCase* Case = FindCase(Cases, TEXT("cache.validityTruthTable")))
	{
		Driven.Add(Case->Id);

		FString MinVersion;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringField(TEXT("minSupportedManifestVersion"), MinVersion);
		}
		// The table's rows are written relative to this floor (the "too old" row sits exactly one
		// below it). If the corpus and this host disagree, every row below is meaningless.
		TestEqual(Case->What(TEXT("corpus floor == this host's MinSupportedManifestVersion")),
			MinVersion, MantlePlaceMinSupportedManifestVersion);

		const TArray<TSharedPtr<FJsonObject>> TruthRows = Rows(*Case, TEXT("rows"));
		TestTrue(Case->What(TEXT("has truth-table rows")), TruthRows.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : TruthRows)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const FString Where = FString::Printf(TEXT("[%s] \"%s\""), *Case->Id, *Name);

			// `null` is UNKNOWN and `absent` is the same thing here; both leave the bHas* flag false.
			// TryGet*Field returns false for a JSON null, which is exactly that distinction.
			FString ExpectedSha;
			const bool bHasExpectedSha = Row->TryGetStringField(TEXT("expectedSha256"), ExpectedSha);
			double ExpectedSize = 0.0;
			const bool bHasExpectedSize = Row->TryGetNumberField(TEXT("expectedSizeBytes"), ExpectedSize);
			// The truth table deliberately SPANS the era break: rows at the floor carry the semver
			// string "1.0.0", and the "too old" rows carry the integer 19 — the pre-history's top,
			// which is the neighbour immediately below a semver floor in the total order. So both
			// JSON shapes are read here, and reading only one would silently skip half the table.
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

		// (file present?, validity) -> the list-row state the panel renders.
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

	// ========================================================================================
	// Host-local: presigned-URL expiry math + the cache.json sidecar
	// ========================================================================================
	// The sidecar is this plugin's own on-disk format (no other host reads it) and the expiry
	// helper is engine FDateTime plumbing, so neither belongs in a cross-host corpus (DOC-06).
	{
		FDateTime Expiry;
		TestTrue(TEXT("ParseExpiry parses ISO-8601"),
			FLogic::ParseExpiry(TEXT("2026-06-20T00:00:00.000Z"), Expiry));
		TestFalse(TEXT("ParseExpiry rejects empty"), FLogic::ParseExpiry(TEXT(""), Expiry));

		const FDateTime DayBefore(2026, 6, 19, 0, 0, 0);
		const FDateTime AtExpiry(2026, 6, 20, 0, 0, 0);
		const FDateTime JustInsideSkew(2026, 6, 19, 23, 59, 30); // 30s before expiry, skew 60s
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

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
