// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultLogic.h"
#include "MantlePlaceVaultTypes.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

// The vault protocol vectors — list/download/materialize response shapes, the status word buckets,
// the error-key precedence, the targeted token list — are read from the shared conformance corpus
// at tools/manifest-conformance/corpus/vault/ (HPS-40). A .NET host asserts the same bytes.
//
// Unlike the manifest group, these cases each drive a DIFFERENT parser, so they are dispatched by
// id rather than in one loop. `Driven` + UndrivenCases is what stops a case added to the corpus
// from being quietly ignored here (HPS-41).

namespace
{
using namespace MantlePlaceConformanceCorpus;

/** Expectation keys this suite knows how to assert. The list no longer proves coverage by itself —
 *  the per-case AssertedKeys tracking does (HPS-46) — it is kept to tell "unknown key" apart from
 *  "known key declared with a type this host could not read" in the failure message. */
const TCHAR* const ConsumedExpectationKeys[] = {
	TEXT("itemCount"),
	TEXT("items"),
	TEXT("orderIds"),
	TEXT("warningCount"),
	TEXT("url"),
	TEXT("expiresAt"),
	TEXT("jobId"),
	TEXT("alreadyRunning"),
	TEXT("tierLabels"),
	TEXT("outcome"),
	TEXT("tokens"),
};

/** Parse a JSON body and read a string field; returns "" if absent. */
FString VaultReadStringField(const FString& JsonStr, const TCHAR* Field)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
	if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
	{
		FString Value;
		Root->TryGetStringField(Field, Value);
		return Value;
	}
	return FString();
}

/** Parse a JSON body and read a top-level string array; empty when absent or wrong-typed. */
TArray<FString> VaultReadStringArrayField(const FString& JsonStr, const TCHAR* Field)
{
	TArray<FString> Values;
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
	if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
	{
		const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
		if (Root->TryGetArrayField(Field, Array) && Array != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& Entry : *Array)
			{
				FString Text;
				if (Entry.IsValid() && Entry->TryGetString(Text))
				{
					Values.Add(MoveTemp(Text));
				}
			}
		}
	}
	return Values;
}

// Both mappers carry an explicit case per enumerator and NO default: the corpus legitimately
// expects "Unknown" for some rows, so "Unknown" must map from the real Unknown enumerator — a
// default: that produced it would let an unmapped enumerator silently equal an expected value
//. Anything unmapped comes back as a sentinel no corpus row will ever state.

FString StatusName(EMantlePlaceVaultBundleStatus Status)
{
	switch (Status)
	{
		case EMantlePlaceVaultBundleStatus::Available:      return TEXT("Available");
		case EMantlePlaceVaultBundleStatus::RefreshPending:  return TEXT("RefreshPending");
		case EMantlePlaceVaultBundleStatus::Refunded:        return TEXT("Refunded");
		case EMantlePlaceVaultBundleStatus::Failed:          return TEXT("Failed");
		case EMantlePlaceVaultBundleStatus::Unknown:         return TEXT("Unknown");
	}
	return TEXT("UNMAPPED");
}

FString StartOutcomeName(EMantlePlaceMaterializeStartOutcome Outcome)
{
	switch (Outcome)
	{
	case EMantlePlaceMaterializeStartOutcome::Started:
		return TEXT("Started");
	case EMantlePlaceMaterializeStartOutcome::Joined:
		return TEXT("Joined");
	case EMantlePlaceMaterializeStartOutcome::NothingToDo:
		return TEXT("NothingToDo");
	case EMantlePlaceMaterializeStartOutcome::Queued:
		return TEXT("Queued");
	default:
		return TEXT("<unmapped>");
	}
}

FString StateName(EMantlePlaceMaterializeState State)
{
	switch (State)
	{
		case EMantlePlaceMaterializeState::Pending:    return TEXT("Pending");
		case EMantlePlaceMaterializeState::Processing: return TEXT("Processing");
		case EMantlePlaceMaterializeState::Complete:   return TEXT("Complete");
		case EMantlePlaceMaterializeState::Failed:     return TEXT("Failed");
		case EMantlePlaceMaterializeState::Unknown:    return TEXT("Unknown");
	}
	return TEXT("UNMAPPED");
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultLogicTest,
	"MantlePlace.Vault.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceVaultLogic;
	using EStatus = EMantlePlaceVaultBundleStatus;
	using EState = EMantlePlaceMaterializeState;

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("vault"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}
	TSet<FString> Driven;

	/** Fetch a case by id, recording it as driven. Null (and an error) if the corpus lost it. */
	auto Take = [this, &Cases, &Driven](const TCHAR* Id) -> const FCase*
	{
		const FCase* Found = FindCase(Cases, Id);
		if (Found == nullptr)
		{
			AddError(FString::Printf(TEXT("corpus case '%s' has gone missing from the vault group"), Id));
			return nullptr;
		}
		Driven.Add(Found->Id);
		return Found;
	};

	// --- list: full + legacy rows -----------------------------------------------------------
	// The discrimination that matters: a legacy row's null sidecar means UNKNOWN, and the full
	// row's elevation flag is known-and-FALSE. A host that reads "layers object present" as "all
	// true" passes a laxer check and is wrong.
	if (const FCase* Case = Take(TEXT("vault.list.fullAndLegacy")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")), FLogic::ParseListResponse(Case->Payload, Items, Error));

		int32 ExpectedCount = 0;
		WantsInt(*Case, TEXT("itemCount"), ExpectedCount);
		TestEqual(Case->What(TEXT("itemCount")), Items.Num(), ExpectedCount);

		TArray<TSharedPtr<FJsonObject>> ExpectedItems;
		if (WantsObjectRows(*Case, TEXT("items"), ExpectedItems) && Items.Num() == ExpectedItems.Num())
		{
			for (int32 Index = 0; Index < Items.Num(); ++Index)
			{
				const TSharedPtr<FJsonObject> Row = ExpectedItems[Index];
				const FMantlePlaceVaultItem& Item = Items[Index];
				// The row's path in the corpus, not merely a label: every read below records against
				// it, so an unread key fails naming `items[1].hasManifestVersion` (HPS-46b).
				const FString Path = FString::Printf(TEXT("items[%d]"), Index);
				const FString Where = FString::Printf(TEXT("[%s] %s"), *Case->Id, *Path);

				// Every read is strictly typed and recorded. No TryGet*Field below this line: it
				// coerces across JSON types, and a coerced read asserts nothing while counting as
				// coverage — the asserted-keys bug, one level down.
				FString Text;
				bool bFlag = false;
				double Number = 0.0;

				if (ExpectRowString(*Case, Path, Row, TEXT("orderId"), Text))
				{
					TestEqual(Where + TEXT(".orderId"), Item.OrderId, Text);
				}
				if (ExpectRowString(*Case, Path, Row, TEXT("status"), Text))
				{
					TestEqual(Where + TEXT(".status"), StatusName(Item.Status), Text);
				}
				if (ExpectRowBool(*Case, Path, Row, TEXT("layersKnown"), bFlag))
				{
					TestTrue(Where + TEXT(".layersKnown"), Item.bLayersKnown == bFlag);
				}
				if (ExpectRowBool(*Case, Path, Row, TEXT("hasManifestVersion"), bFlag))
				{
					TestTrue(Where + TEXT(".hasManifestVersion"), Item.bHasManifestVersion == bFlag);
				}
				if (ExpectRowBool(*Case, Path, Row, TEXT("hasSizeBytes"), bFlag))
				{
					TestTrue(Where + TEXT(".hasSizeBytes"), Item.bHasSizeBytes == bFlag);
				}
				if (ExpectRowBool(*Case, Path, Row, TEXT("hasSha256"), bFlag))
				{
					TestTrue(Where + TEXT(".hasSha256"), Item.bHasSha256 == bFlag);
				}
				if (ExpectRowBool(*Case, Path, Row, TEXT("downloadable"), bFlag))
				{
					TestTrue(Where + TEXT(".downloadable"), FLogic::IsDownloadable(Item) == bFlag);
				}

				// Values, not just the known/unknown flags. A row states these only when the sidecar
				// actually knows them — the legacy row's nulls mean there is nothing to assert.
				if (ExpectRowString(*Case, Path, Row, TEXT("aoiLabel"), Text))
				{
					TestEqual(Where + TEXT(".aoiLabel"), Item.AoiLabel, Text);
				}
				if (ExpectRowString(*Case, Path, Row, TEXT("createdAt"), Text))
				{
					TestEqual(Where + TEXT(".createdAt"), Item.CreatedAt, Text);
				}
				if (ExpectRowString(*Case, Path, Row, TEXT("sha256"), Text))
				{
					TestEqual(Where + TEXT(".sha256"), Item.Sha256, Text);
				}
				if (ExpectRowNumber(*Case, Path, Row, TEXT("areaKm2"), Number))
				{
					TestEqual(Where + TEXT(".areaKm2"), Item.AreaKm2, Number, 1e-9);
				}
				if (ExpectRowVersion(*Case, Path, Row, TEXT("manifestVersion"), Text))
				{
					TestEqual(Where + TEXT(".manifestVersion"), Item.ManifestVersion, Text);
				}
				if (ExpectRowNumber(*Case, Path, Row, TEXT("sizeBytes"), Number))
				{
					TestTrue(Where + TEXT(".sizeBytes"), Item.SizeBytes == static_cast<int64>(Number));
				}

				const TArray<TSharedPtr<FJsonValue>>* Formats = nullptr;
				if (ExpectRowArray(*Case, Path, Row, TEXT("formats"), Formats))
				{
					TArray<FString> Expected;
					for (int32 Slot = 0; Slot < Formats->Num(); ++Slot)
					{
						FString Format;
						if (ExpectElementString(*Case,
								FString::Printf(TEXT("%s.formats[%d]"), *Path, Slot), (*Formats)[Slot], Format))
						{
							Expected.Add(Format);
						}
					}
					TestEqual(Where + TEXT(".formats"),
						FString::Join(Item.Formats, TEXT(",")), FString::Join(Expected, TEXT(",")));
				}

				// The per-format download menu, including byteSize 0 = UNRECORDED (not an empty file).
				const TArray<TSharedPtr<FJsonValue>>* DownloadFormats = nullptr;
				if (ExpectRowArray(*Case, Path, Row, TEXT("downloadFormats"), DownloadFormats))
				{
					TestEqual(Where + TEXT(".downloadFormats count"),
						Item.DownloadFormats.Num(), DownloadFormats->Num());
					const int32 Count = FMath::Min(Item.DownloadFormats.Num(), DownloadFormats->Num());
					for (int32 Slot = 0; Slot < Count; ++Slot)
					{
						const FString SlotPath = FString::Printf(TEXT("%s.downloadFormats[%d]"), *Path, Slot);
						const TSharedPtr<FJsonObject> Format = ExpectElementObject((*DownloadFormats)[Slot]);
						if (ExpectRowString(*Case, SlotPath, Format, TEXT("format"), Text))
						{
							TestEqual(SlotPath + TEXT(".format"), Item.DownloadFormats[Slot].Format, Text);
						}
						if (ExpectRowNumber(*Case, SlotPath, Format, TEXT("byteSize"), Number))
						{
							TestTrue(SlotPath + TEXT(".byteSize"),
								Item.DownloadFormats[Slot].ByteSize == static_cast<int64>(Number));
						}
					}
				}

				TSharedPtr<FJsonObject> Layers;
				if (ExpectRowObject(*Case, Path, Row, TEXT("layers"), Layers))
				{
					const FString LayersPath = ExpectPath(Path, TEXT("layers"));
					if (ExpectRowBool(*Case, LayersPath, Layers, TEXT("imagery"), bFlag))
					{
						TestTrue(Where + TEXT(".layers.imagery"), Item.Layers.bImagery == bFlag);
					}
					if (ExpectRowBool(*Case, LayersPath, Layers, TEXT("basemap"), bFlag))
					{
						TestTrue(Where + TEXT(".layers.basemap"), Item.Layers.bBasemap == bFlag);
					}
					if (ExpectRowBool(*Case, LayersPath, Layers, TEXT("elevation"), bFlag))
					{
						TestTrue(Where + TEXT(".layers.elevation"), Item.Layers.bElevation == bFlag);
					}
				}
			}
		}
		else
		{
			AddError(Case->What(TEXT("could not line the parsed items up with expectations.items")));
		}
	}

	// --- list: the tier label, which only this host can compute ------------------------------
	// Its whole discriminator is the presence of `glb` — the Unreal terrain mesh — so it rides an
	// `appliesTo` case rather than the host-invariant listing above (HPS-41). All three branches,
	// not just the two rows the listing happens to have: a Base bundle is known-and-partial, an
	// empty formats list is the base_on_demand marker where nothing is known yet, and collapsing
	// the two is the difference between materializing and importing.
	if (const FCase* Case = Take(TEXT("vault.list.tierLabel")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")), FLogic::ParseListResponse(Case->Payload, Items, Error));

		int32 ExpectedCount = -1;
		WantsInt(*Case, TEXT("itemCount"), ExpectedCount);
		TestEqual(Case->What(TEXT("itemCount")), Items.Num(), ExpectedCount);

		TArray<FString> ExpectedLabels;
		if (WantsStringArray(*Case, TEXT("tierLabels"), ExpectedLabels)
			&& Items.Num() == ExpectedLabels.Num())
		{
			for (int32 Index = 0; Index < Items.Num(); ++Index)
			{
				TestEqual(FString::Printf(TEXT("[%s] tierLabels[%d]"), *Case->Id, Index),
					FLogic::DeriveTierLabel(Items[Index]), ExpectedLabels[Index]);
			}
		}
		else
		{
			AddError(Case->What(TEXT("could not line the parsed items up with expectations.tierLabels")));
		}
	}

	// --- list: empty, malformed rows skipped, wrong top-level key ---------------------------
	if (const FCase* Case = Take(TEXT("vault.list.empty")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		int32 ExpectedCount = -1;
		WantsInt(*Case, TEXT("itemCount"), ExpectedCount);
		TestEqual(Case->What(TEXT("itemCount")), Items.Num(), ExpectedCount);
	}

	if (const FCase* Case = Take(TEXT("vault.list.skipsMalformedRows")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		TArray<FString> Warnings;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")),
			FLogic::ParseListResponse(Case->Payload, Items, Error, &Warnings));

		int32 ExpectedCount = -1;
		WantsInt(*Case, TEXT("itemCount"), ExpectedCount);
		TestEqual(Case->What(TEXT("itemCount")), Items.Num(), ExpectedCount);

		int32 ExpectedWarnings = -1;
		WantsInt(*Case, TEXT("warningCount"), ExpectedWarnings);
		TestEqual(Case->What(TEXT("warningCount")), Warnings.Num(), ExpectedWarnings);

		TArray<FString> ExpectedIds;
		if (WantsStringArray(*Case, TEXT("orderIds"), ExpectedIds))
		{
			TArray<FString> Actual;
			for (const FMantlePlaceVaultItem& Item : Items)
			{
				Actual.Add(Item.OrderId);
			}
			TestEqual(Case->What(TEXT("orderIds")), FString::Join(Actual, TEXT(",")),
				FString::Join(ExpectedIds, TEXT(",")));
		}
	}

	if (const FCase* Case = Take(TEXT("vault.list.wrongTopLevelKey")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error names the missing key")), Error.Contains(Case->ErrorContains));
	}

	// Unparseable listing bytes fail closed with a reason — never a crash, never a blank vault.
	if (const FCase* Case = Take(TEXT("vault.reject.notJson")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		TestFalse(Case->What(TEXT("rejection states a reason")), Error.IsEmpty());
		TestEqual(Case->What(TEXT("no items survive")), Items.Num(), 0);
	}

	// --- download: presigned url + the entitlement-error fallback ---------------------------
	if (const FCase* Case = Take(TEXT("vault.download.presigned")))
	{
		FMantlePlacePresignedDownload Download;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")),
			FLogic::ParseDownloadResponse(Case->Payload, Download, Error));
		FString Expected;
		if (WantsString(*Case, TEXT("url"), Expected))
		{
			TestEqual(Case->What(TEXT("url")), Download.Url, Expected);
		}
		if (WantsString(*Case, TEXT("expiresAt"), Expected))
		{
			TestEqual(Case->What(TEXT("expiresAt")), Download.ExpiresAt, Expected);
		}
	}

	if (const FCase* Case = Take(TEXT("vault.download.missingUrl")))
	{
		FMantlePlacePresignedDownload Download;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")),
			FLogic::ParseDownloadResponse(Case->Payload, Download, Error));
		// The body carries a real reason; surfacing "missing 'url'" instead would hide it.
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error body message wins over the generic message")),
			Error.Contains(Case->ErrorContains));
	}

	// --- materialize: start responses -------------------------------------------------------
	for (const TCHAR* Id : { TEXT("vault.materialize.started"), TEXT("vault.materialize.alreadyRunning") })
	{
		if (const FCase* Case = Take(Id))
		{
			FString JobId;
			bool bAlreadyRunning = false;
			FString Error;
			TestTrue(Case->What(TEXT("accepted")),
				FLogic::ParseMaterializeStartResponse(Case->Payload, JobId, bAlreadyRunning, Error));

			FString ExpectedJobId;
			if (WantsString(*Case, TEXT("jobId"), ExpectedJobId))
			{
				TestEqual(Case->What(TEXT("jobId")), JobId, ExpectedJobId);
			}
			bool bExpected = false;
			if (WantsBool(*Case, TEXT("alreadyRunning"), bExpected))
			{
				TestTrue(Case->What(TEXT("alreadyRunning")), bAlreadyRunning == bExpected);
			}
		}
	}

	// A start body with neither jobId nor activeJobId fails closed and surfaces the platform's own
	// message through the HPS-48 precedence.
	if (const FCase* Case = Take(TEXT("vault.materialize.startNoJobId")))
	{
		FString JobId;
		bool bAlreadyRunning = false;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")),
			FLogic::ParseMaterializeStartResponse(Case->Payload, JobId, bAlreadyRunning, Error));
		TestTrue(Case->What(TEXT("no job id is invented")), JobId.IsEmpty());
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error body message surfaces")), Error.Contains(Case->ErrorContains));
	}

	// --- materialize: the shapes that name NO job -------------------------------------------
	// STOP: two of the platform's five start shapes are successes carrying no job id, and a third
	// carries it under `activeJobId` with no `jobId` at all. Inferring failure from a missing `jobId`
	// is what left the Revit host unable to import any bundle with nothing left to build; this host
	// had the identical hole, masked only because a complete bundle skips materialize entirely.
	for (const TCHAR* Id : {
	         TEXT("vault.materialize.noop"),
	         TEXT("vault.materialize.queued"),
	         TEXT("vault.materialize.coalesced"),
	         TEXT("vault.materialize.activeJobWithoutId") })
	{
		if (const FCase* Case = Take(Id))
		{
			FMantlePlaceMaterializeStart Start;
			FString Error;
			TestTrue(Case->What(TEXT("accepted")),
			         FLogic::ParseMaterializeStartResponse(Case->Payload, Start, Error));

			FString Expected;
			if (WantsString(*Case, TEXT("outcome"), Expected))
			{
				TestEqual(Case->What(TEXT("outcome")), StartOutcomeName(Start.Outcome), Expected);
			}
			if (WantsString(*Case, TEXT("jobId"), Expected))
			{
				TestEqual(Case->What(TEXT("jobId")), Start.JobId, Expected);
			}
			bool bExpected = false;
			if (WantsBool(*Case, TEXT("alreadyRunning"), bExpected))
			{
				TestTrue(Case->What(TEXT("alreadyRunning")), Start.IsAlreadyRunning() == bExpected);
			}
			TArray<FString> ExpectedTokens;
			if (WantsStringArray(*Case, TEXT("tokens"), ExpectedTokens))
			{
				TestEqual(Case->What(TEXT("tokens")), Start.Tokens, ExpectedTokens);
			}
		}
	}

	// --- materialize: the delivery-state document -------------------------------------------
	// Polling this endpoint returns delivered/notDelivered/activeJob and NO status word, so
	// completion is derived. This table is that derivation.
	if (const FCase* Case = Take(TEXT("vault.materialize.deliveryVectors")))
	{
		const TArray<FString> Requested = VaultReadStringArrayField(Case->Payload, TEXT("requested"));
		TestTrue(Case->What(TEXT("names a requested set")), Requested.Num() > 0);

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (int32 Index = 0; Index < Vectors.Num(); ++Index)
		{
			const TSharedPtr<FJsonObject>& Row = Vectors[Index];
			const FString Body = RowBodyAsText(Row);
			const FString Where = FString::Printf(TEXT("[%s] vectors[%d]"), *Case->Id, Index);
			const bool bShouldParse = RowBool(Row, TEXT("parseSucceeds"), true);

			FMantlePlaceMaterializeStatus Status;
			FString Error;
			const bool bParsed = FLogic::ParseMaterializeStatus(Body, Requested, Status, Error);
			TestTrue(Where + TEXT(" parseSucceeds"), bParsed == bShouldParse);

			if (bShouldParse && bParsed)
			{
				TestEqual(Where + TEXT(" state"), StateName(Status.State), RowString(Row, TEXT("state")));
				const double ExpectedFraction = RowNumber(Row, TEXT("fraction"), -1.0);
				TestEqual(Where + TEXT(" fraction"),
				          static_cast<double>(Status.Fraction), ExpectedFraction, 1e-4);

				FString Expected;
				if (Row.IsValid() && Row->TryGetStringField(TEXT("jobId"), Expected))
				{
					TestEqual(Where + TEXT(" jobId"), Status.JobId, Expected);
				}
				if (Row.IsValid() && Row->TryGetStringField(TEXT("messageContains"), Expected))
				{
					TestTrue(Where + TEXT(" messageContains"), Status.Message.Contains(Expected));
				}

				const TArray<TSharedPtr<FJsonValue>>* Gaps = nullptr;
				if (Row.IsValid() && Row->TryGetArrayField(TEXT("unproducible"), Gaps) && Gaps != nullptr)
				{
					TestEqual(Where + TEXT(" unproducible count"), Status.Unproducible.Num(), Gaps->Num());
					for (int32 Gap = 0; Gap < Gaps->Num() && Gap < Status.Unproducible.Num(); ++Gap)
					{
						FString Token;
						(*Gaps)[Gap]->TryGetString(Token);
						TestEqual(Where + TEXT(" unproducible token"), Status.Unproducible[Gap].Token, Token);
					}
				}
			}
			else if (!bShouldParse)
			{
				const FString Contains = RowString(Row, TEXT("errorContains"));
				if (!Contains.IsEmpty())
				{
					TestTrue(Where + TEXT(" errorContains"), Error.Contains(Contains));
				}
			}
		}
	}

	// --- materialize: status vectors --------------------------------------------------------
	if (const FCase* Case = Take(TEXT("vault.materialize.statusVectors")))
	{
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (int32 Index = 0; Index < Vectors.Num(); ++Index)
		{
			const TSharedPtr<FJsonObject>& Row = Vectors[Index];
			const FString Body = RowBodyAsText(Row);
			const FString Where = FString::Printf(TEXT("[%s] vectors[%d]"), *Case->Id, Index);
			const bool bShouldParse = RowBool(Row, TEXT("parseSucceeds"), true);

			FMantlePlaceMaterializeStatus Status;
			FString Error;
			const bool bParsed = FLogic::ParseMaterializeStatus(Body, Status, Error);
			TestTrue(Where + TEXT(" parseSucceeds"), bParsed == bShouldParse);

			if (bShouldParse && bParsed)
			{
				TestEqual(Where + TEXT(" state"), StateName(Status.State), RowString(Row, TEXT("state")));
				// -1 is INDETERMINATE, not zero: a progress bar must not render 0% for "unknown".
				const double ExpectedFraction = RowNumber(Row, TEXT("fraction"), -1.0);
				TestEqual(Where + TEXT(" fraction"),
					static_cast<double>(Status.Fraction), ExpectedFraction, 1e-4);

				// jobId and the failure message ride the body and must be SURFACED, not merely
				// tolerated — both went unasserted before this suite. Stated only on the rows that
				// carry them.
				FString Expected;
				if (Row.IsValid() && Row->TryGetStringField(TEXT("jobId"), Expected))
				{
					TestEqual(Where + TEXT(" jobId"), Status.JobId, Expected);
				}
				if (Row.IsValid() && Row->TryGetStringField(TEXT("message"), Expected))
				{
					TestEqual(Where + TEXT(" message"), Status.Message, Expected);
				}
			}
			else if (!bShouldParse)
			{
				const FString Contains = RowString(Row, TEXT("errorContains"));
				if (!Contains.IsEmpty())
				{
					TestTrue(Where + TEXT(" errorContains"), Error.Contains(Contains));
				}
			}
		}
	}

	// --- status words: both enum vocabularies, incl. the synonyms and the Unknown default ----
	if (const FCase* Case = Take(TEXT("vault.statusWordBuckets")))
	{
		const TSharedPtr<FJsonObject>* BundleStatus = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("bundleStatus"), BundleStatus)
			&& BundleStatus != nullptr)
		{
			for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*BundleStatus)->Values)
			{
				TestEqual(
					FString::Printf(TEXT("[%s] bundleStatus[\"%s\"]"), *Case->Id, *Pair.Key),
					StatusName(FLogic::ParseStatus(Pair.Key)),
					Pair.Value->AsString());
			}
		}
		else
		{
			AddError(Case->What(TEXT("bundleStatus table missing")));
		}

		const TSharedPtr<FJsonObject>* MaterializeState = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("materializeState"), MaterializeState)
			&& MaterializeState != nullptr)
		{
			for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*MaterializeState)->Values)
			{
				TArray<FString> Words;
				(*MaterializeState)->TryGetStringArrayField(Pair.Key, Words);
				for (const FString& Word : Words)
				{
					TestEqual(
						FString::Printf(TEXT("[%s] materializeState[\"%s\"]"), *Case->Id, *Word),
						StateName(FLogic::ParseMaterializeState(Word)),
						Pair.Key);
					// The table says matching is case-insensitive; assert it rather than trust it.
					TestEqual(
						FString::Printf(TEXT("[%s] materializeState[\"%s\"] uppercased"), *Case->Id, *Word),
						StateName(FLogic::ParseMaterializeState(Word.ToUpper())),
						Pair.Key);
				}
			}
		}
		else
		{
			AddError(Case->What(TEXT("materializeState table missing")));
		}

		TestTrue(Case->What(TEXT("an unlisted word is Unknown, never an error")),
			FLogic::ParseMaterializeState(TEXT("definitely-not-a-state")) == EState::Unknown
				&& FLogic::ParseStatus(TEXT("definitely-not-a-status")) == EStatus::Unknown);
	}

	// --- error body: which key wins, and the code that rides along ---------------------------
	if (const FCase* Case = Take(TEXT("vault.errorBodyPrecedence")))
	{
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (int32 Index = 0; Index < Vectors.Num(); ++Index)
		{
			const TSharedPtr<FJsonObject>& Row = Vectors[Index];
			const FString Where = FString::Printf(TEXT("[%s] vectors[%d]"), *Case->Id, Index);
			const bool bShouldParse = RowBool(Row, TEXT("parseSucceeds"), true);

			FString Message;
			FString Code;
			const bool bParsed = FLogic::ParseErrorBody(RowBodyAsText(Row), Message, Code);
			TestTrue(Where + TEXT(" parseSucceeds"), bParsed == bShouldParse);
			if (bShouldParse && bParsed)
			{
				TestEqual(Where + TEXT(" message"), Message, RowString(Row, TEXT("message")));
				TestEqual(Where + TEXT(" code"), Code, RowString(Row, TEXT("code")));
			}
		}

		// The stated order itself, driven synthetically: build a body carrying every key from
		// position i onward and assert the key at i is the one whose value surfaces. Mirrors the
		// auth suite's errorPrecedence driver — HPS-48 makes the two parsers share ONE order, so
		// they earn the same proof.
		TArray<FString> Precedence;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("keyPrecedence"), Precedence);
		}
		TestTrue(Case->What(TEXT("states a keyPrecedence")), Precedence.Num() > 0);
		for (int32 Index = 0; Index < Precedence.Num(); ++Index)
		{
			const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
			for (int32 Rest = Index; Rest < Precedence.Num(); ++Rest)
			{
				Body->SetStringField(Precedence[Rest], FString::Printf(TEXT("value-of-%s"), *Precedence[Rest]));
			}
			FString Text;
			const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
			FJsonSerializer::Serialize(Body, Writer);

			FString Message;
			FString Code;
			TestTrue(FString::Printf(TEXT("[%s] keyPrecedence[%d] parses"), *Case->Id, Index),
				FLogic::ParseErrorBody(Text, Message, Code));
			TestEqual(
				FString::Printf(TEXT("[%s] '%s' wins over everything below it"), *Case->Id, *Precedence[Index]),
				Message, FString::Printf(TEXT("value-of-%s"), *Precedence[Index]));
		}
	}

	// --- materialize token list: the explicit, client-owned scope (HPS-23) -------------------
	// "all" is a legitimate user-facing scope and stays the server keyword; "unreal" must send the
	// explicit token ARRAY, because the plugin — not the server — owns which layers a UE import needs.
	if (const FCase* Case = Take(TEXT("vault.materializeTokenList")))
	{
		TArray<FString> ExpectedTokens;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("tokens"), ExpectedTokens);
		}
		TestEqual(Case->What(TEXT("targeted token count")),
			FLogic::TargetedImportTokens().Num(), ExpectedTokens.Num());
		for (const FString& Token : ExpectedTokens)
		{
			TestTrue(FString::Printf(TEXT("[%s] sends token \"%s\""), *Case->Id, *Token),
				FLogic::TargetedImportTokens().Contains(Token));
		}

		// The body actually put on the wire carries those same tokens, in order.
		TArray<FString> Sent;
		{
			TSharedPtr<FJsonObject> Root;
			const FString Body = FLogic::BuildMaterializeBody(TEXT("Unreal")); // case-insensitive scope
			const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(Body);
			TestTrue(Case->What(TEXT("materialize body parses")),
				FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid());
			if (Root.IsValid())
			{
				Root->TryGetStringArrayField(TEXT("tokens"), Sent);
			}
		}
		TestEqual(Case->What(TEXT("body tokens match the corpus list")),
			FString::Join(Sent, TEXT(",")), FString::Join(ExpectedTokens, TEXT(",")));

		// The wire bodies themselves come from the corpus, not from literals here: the explicit
		// scope sends the token ARRAY the corpus states, and "all" sends whatever keyword the
		// corpus states — nothing about either shape is this suite's to hardcode.
		const TSharedPtr<FJsonObject>* ExplicitBody = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("bodyForExplicitScope"), ExplicitBody)
			&& ExplicitBody != nullptr)
		{
			TArray<FString> BodyTokens;
			(*ExplicitBody)->TryGetStringArrayField(TEXT("tokens"), BodyTokens);
			TestEqual(Case->What(TEXT("explicit-scope body matches bodyForExplicitScope")),
				FString::Join(Sent, TEXT(",")), FString::Join(BodyTokens, TEXT(",")));
		}
		else
		{
			AddError(Case->What(TEXT("bodyForExplicitScope missing")));
		}
		const TSharedPtr<FJsonObject>* AllBody = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("bodyForAllScope"), AllBody)
			&& AllBody != nullptr)
		{
			TestEqual(Case->What(TEXT("all-scope body matches bodyForAllScope")),
				VaultReadStringField(FLogic::BuildMaterializeBody(TEXT("all")), TEXT("tokens")),
				RowString(*AllBody, TEXT("tokens")));
		}
		else
		{
			AddError(Case->What(TEXT("bodyForAllScope missing")));
		}

		TArray<FString> ValidScopes;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("validScopes"), ValidScopes);
		}
		for (const FString& Scope : ValidScopes)
		{
			TestTrue(FString::Printf(TEXT("[%s] scope \"%s\" is valid"), *Case->Id, *Scope),
				FLogic::IsValidMaterializeScope(Scope));
		}

		// The scope known-answer rows: the case-insensitivity rows and the invalids are the
		// normative part (lowercase-only vectors left them unpinned).
		const TArray<TSharedPtr<FJsonObject>> ScopeVectors = Rows(*Case, TEXT("scopeVectors"));
		TestTrue(Case->What(TEXT("has scopeVectors")), ScopeVectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : ScopeVectors)
		{
			const FString Scope = RowString(Row, TEXT("scope"));
			const bool bValid = RowBool(Row, TEXT("valid"));
			TestTrue(
				FString::Printf(TEXT("[%s] IsValidMaterializeScope(\"%s\") == %s"),
					*Case->Id, *Scope, bValid ? TEXT("true") : TEXT("false")),
				FLogic::IsValidMaterializeScope(Scope) == bValid);
		}
		TestFalse(Case->What(TEXT("an unlisted scope is rejected")),
			FLogic::IsValidMaterializeScope(TEXT("buildings")));
	}

	// --- the presign request body -----------------------------------------------------------
	// The corpus pinned presign RESPONSES from the start and never the request, so a host could ask
	// for the wrong thing and stay green through every gate. This host asked for "glb" and called it
	// the whole-bundle zip: that token ALSO names a real artifact format, so the platform hands back
	// the mesh whenever the order carries one and only falls through to the archive when it does
	// not. It was right by luck of the data, and the cache verifies against the archive's sha256.
	if (const FCase* Case = Take(TEXT("vault.downloadRequestBody")))
	{
		const FString WholeBundle = Case->PayloadObject.IsValid()
		                                ? RowString(Case->PayloadObject, TEXT("wholeBundleFormat"))
		                                : FString();
		const FString DeprecatedAlias = Case->PayloadObject.IsValid()
		                                    ? RowString(Case->PayloadObject, TEXT("deprecatedWholeBundleAlias"))
		                                    : FString();

		TestEqual(Case->What(TEXT("this host names the archive with the corpus's token")),
		          FLogic::WholeBundleFormat(), WholeBundle);
		TestFalse(Case->What(TEXT("and never with the deprecated alias")),
		          FLogic::WholeBundleFormat().Equals(DeprecatedAlias, ESearchCase::IgnoreCase));

		// The body on the wire comes from the corpus, not from a literal here.
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Case->PayloadObject.IsValid() && Case->PayloadObject->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			TestEqual(Case->What(TEXT("the presign body matches the corpus body")),
			          VaultReadStringField(FLogic::BuildDownloadBody(FLogic::WholeBundleFormat()), TEXT("format")),
			          RowString(*Body, TEXT("format")));
		}
		else
		{
			AddError(Case->What(TEXT("body missing")));
		}

		const TArray<TSharedPtr<FJsonObject>> FormatVectors = Rows(*Case, TEXT("formatVectors"));
		TestTrue(Case->What(TEXT("has formatVectors")), FormatVectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : FormatVectors)
		{
			const FString Format = RowString(Row, TEXT("format"));
			const bool bPresignable = RowBool(Row, TEXT("presignable"));
			const bool bWholeBundle = RowBool(Row, TEXT("wholeBundle"));

			TestTrue(
			    FString::Printf(TEXT("[%s] IsPresignableFormat(\"%s\") == %s"),
			                    *Case->Id, *Format, bPresignable ? TEXT("true") : TEXT("false")),
			    FLogic::IsPresignableFormat(Format) == bPresignable);

			TestTrue(
			    FString::Printf(TEXT("[%s] \"%s\" names the whole archive: %s"),
			                    *Case->Id, *Format, bWholeBundle ? TEXT("true") : TEXT("false")),
			    Format.Equals(FLogic::WholeBundleFormat(), ESearchCase::IgnoreCase) == bWholeBundle);
		}
	}

	// --- HPS-46 asserted-keys guard ---------------------------------------------------------
	// Consumption is proven by what was ASSERTED, not by the allow-list alone: a declared key
	// nothing read — unknown, mistyped, or on an assertion path that never ran — fails.
	for (const FCase& Case : Cases)
	{
		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

		// And below the top level: recording `items` proves the array was reached, never
		// that anything inside it was read (HPS-46b).
		for (const FString& Problem : UnassertedNestedExpectations(Case))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the vault group but nothing in this suite drives it. ")
			TEXT("A host consumes every case in the groups it claims (HPS-41)."),
			*Missing));
	}

	// --- Host-local API surface the corpus does not specify ----------------------------------
	// URL construction is per-host plumbing (base-URL config, percent-encoding rules), not part of
	// the cross-host wire contract the corpus pins.
	{
		TestEqual(TEXT("List URL from clean base"),
			FLogic::BuildListBundlesUrl(TEXT("https://mantle.place")),
			FString(TEXT("https://mantle.place/api/v1/vault/bundles")));
		TestEqual(TEXT("List URL strips trailing slash + trims whitespace"),
			FLogic::BuildListBundlesUrl(TEXT("  https://mantle.place//  ")),
			FString(TEXT("https://mantle.place/api/v1/vault/bundles")));
		TestEqual(TEXT("Download URL keeps a UUID intact (hyphens not encoded)"),
			FLogic::BuildDownloadUrl(TEXT("http://localhost:3000"), TEXT("3f285101-0310-425b-b06b-bdb73b025b6a")),
			FString(TEXT("http://localhost:3000/api/v1/vault/bundles/3f285101-0310-425b-b06b-bdb73b025b6a/download")));
		TestEqual(TEXT("Materialize URL strips trailing slash + keeps a UUID intact"),
			FLogic::BuildMaterializeUrl(TEXT("http://localhost:3000/"), TEXT("3f285101-0310-425b-b06b-bdb73b025b6a")),
			FString(TEXT("http://localhost:3000/api/v1/vault/bundles/3f285101-0310-425b-b06b-bdb73b025b6a/materialize")));
		TestEqual(TEXT("Download body format field"),
			VaultReadStringField(FLogic::BuildDownloadBody(TEXT("glb")), TEXT("format")),
			FString(TEXT("glb")));
	}

	// --- IsKnownFormat + the incomplete-bundle heuristic that drives the panel ----------------
	{
		TestTrue(TEXT("glb known"), FLogic::IsKnownFormat(TEXT("glb")));
		TestTrue(TEXT("GLB known (case-insensitive)"), FLogic::IsKnownFormat(TEXT("GLB")));
		TestTrue(TEXT("pmtiles known"), FLogic::IsKnownFormat(TEXT("pmtiles")));
		TestFalse(TEXT("tiff not known"), FLogic::IsKnownFormat(TEXT("tiff")));
		TestFalse(TEXT("empty not known"), FLogic::IsKnownFormat(TEXT("")));

		FMantlePlaceVaultItem Base; // BASE: cesium/imagery formats, no glb terrain mesh
		Base.Formats = { TEXT("pmtiles"), TEXT("geotiff") };
		TestTrue(TEXT("Base bundle (no glb) is incomplete"), FLogic::IsIncompleteBundle(Base));

		FMantlePlaceVaultItem UpperGlb; // glb detection is case-insensitive
		UpperGlb.Formats = { TEXT("GLB") };
		TestFalse(TEXT("GLB (uppercase) counts as complete"), FLogic::IsIncompleteBundle(UpperGlb));

		FMantlePlaceVaultItem Marker; // empty formats = base_on_demand marker
		TestTrue(TEXT("Empty-formats bundle routes through materialize (incomplete)"),
			FLogic::IsIncompleteBundle(Marker));
	}

	// --- The post-download self-heal verdict (vault path) --------------------------------------
	{
		const FString Order = TEXT("3f285101-0310-425b-b06b-bdb73b025b6a");
		TestTrue(TEXT("Readable-but-incomplete manifest with an order id recovers"),
			FLogic::ShouldRecoverMissingUnrealPayload(true, false, Order, false));
		TestFalse(TEXT("A valid manifest imports, never recovers"),
			FLogic::ShouldRecoverMissingUnrealPayload(true, true, Order, false));
		TestFalse(TEXT("An unreadable manifest is the importer's failure to report"),
			FLogic::ShouldRecoverMissingUnrealPayload(false, false, Order, false));
		TestFalse(TEXT("No order id - nothing to materialize against"),
			FLogic::ShouldRecoverMissingUnrealPayload(true, false, TEXT(""), false));
		TestFalse(TEXT("One recovery per run - the second pass fails on the gate, never loops"),
			FLogic::ShouldRecoverMissingUnrealPayload(true, false, Order, true));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
