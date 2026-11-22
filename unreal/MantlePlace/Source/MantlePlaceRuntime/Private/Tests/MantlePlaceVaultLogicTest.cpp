// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultLogic.h"
#include "MantlePlaceVaultTypes.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace
{
using namespace MantlePlaceConformanceCorpus;

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
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultLogicTest,
	"MantlePlace.Vault.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultLogicTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

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
				const FString Path = FString::Printf(TEXT("items[%d]"), Index);
				const FString Where = FString::Printf(TEXT("[%s] %s"), *Case->Id, *Path);

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

	if (const FCase* Case = Take(TEXT("vault.list.empty")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestTrue(Case->What(TEXT("accepted")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		int32 ExpectedCount = -1;
		WantsInt(*Case, TEXT("itemCount"), ExpectedCount);
		TestEqual(Case->What(TEXT("itemCount")), Items.Num(), ExpectedCount);
	}

	for (const TCHAR* ListCaseId :
		{TEXT("vault.list.skipsMalformedRows"), TEXT("vault.list.thumbnailNeverBreaksARow")})
	{
		if (const FCase* Case = Take(ListCaseId))
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
	}

	if (const FCase* Case = Take(TEXT("vault.list.wrongTopLevelKey")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error names the missing key")), Error.Contains(Case->ErrorContains));
	}

	if (const FCase* Case = Take(TEXT("vault.reject.notJson")))
	{
		TArray<FMantlePlaceVaultItem> Items;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")), FLogic::ParseListResponse(Case->Payload, Items, Error));
		TestFalse(Case->What(TEXT("rejection states a reason")), Error.IsEmpty());
		TestEqual(Case->What(TEXT("no items survive")), Items.Num(), 0);
	}

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
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error body message wins over the generic message")),
			Error.Contains(Case->ErrorContains));
	}

	for (const TCHAR* Id : { TEXT("vault.materialize.started"), TEXT("vault.materialize.alreadyRunning") })
	{
		if (const FCase* Case = Take(Id))
		{
			FMantlePlaceMaterializeStart Start;
			FString Error;
			TestTrue(Case->What(TEXT("accepted")),
			         FLogic::ParseMaterializeStartResponse(Case->Payload, Start, Error));

			FString ExpectedJobId;
			if (WantsString(*Case, TEXT("jobId"), ExpectedJobId))
			{
				TestEqual(Case->What(TEXT("jobId")), Start.JobId, ExpectedJobId);
			}
			bool bExpected = false;
			if (WantsBool(*Case, TEXT("alreadyRunning"), bExpected))
			{
				TestTrue(Case->What(TEXT("alreadyRunning")), Start.IsAlreadyRunning() == bExpected);
			}
		}
	}

	if (const FCase* Case = Take(TEXT("vault.materialize.startNoJobId")))
	{
		FMantlePlaceMaterializeStart Start;
		FString Error;
		TestFalse(Case->What(TEXT("rejected")),
		          FLogic::ParseMaterializeStartResponse(Case->Payload, Start, Error));
		TestTrue(Case->What(TEXT("no job id is invented")), Start.JobId.IsEmpty());
		TestFalse(Case->What(TEXT("states an errorContains")), Case->ErrorContains.IsEmpty());
		TestTrue(Case->What(TEXT("error body message surfaces")), Error.Contains(Case->ErrorContains));
	}

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

	if (const FCase* Case = Take(TEXT("vault.materialize.outstandingVectors")))
	{
		const TArray<FString> Requested = VaultReadStringArrayField(Case->Payload, TEXT("requested"));
		TestTrue(Case->What(TEXT("names a requested set")), Requested.Num() > 0);

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (int32 Index = 0; Index < Vectors.Num(); ++Index)
		{
			const TSharedPtr<FJsonObject>& Row = Vectors[Index];
			const FString Where = FString::Printf(TEXT("[%s] vectors[%d]"), *Case->Id, Index);

			FMantlePlaceMaterializeStatus Status;
			FString Error;
			if (!TestTrue(Where + TEXT(" parses"),
			              FLogic::ParseMaterializeStatus(RowBodyAsText(Row), Requested, Status, Error)))
			{
				continue;
			}

			TestEqual(Where + TEXT(" state"), StateName(Status.State), RowString(Row, TEXT("state")));
			TestEqual(Where + TEXT(" outstanding"),
			          Status.Outstanding, RowStrings(Row, TEXT("outstanding")));
		}
	}

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
				const double ExpectedFraction = RowNumber(Row, TEXT("fraction"), -1.0);
				TestEqual(Where + TEXT(" fraction"),
					static_cast<double>(Status.Fraction), ExpectedFraction, 1e-4);

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

		TArray<FString> Sent;
		{
			TSharedPtr<FJsonObject> Root;
			const FString Body = FLogic::BuildMaterializeBody(TEXT("Unreal"));
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

	for (const FCase& Case : Cases)
	{
		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

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

	{
		TestTrue(TEXT("glb known"), FLogic::IsKnownFormat(TEXT("glb")));
		TestTrue(TEXT("GLB known (case-insensitive)"), FLogic::IsKnownFormat(TEXT("GLB")));
		TestTrue(TEXT("pmtiles known"), FLogic::IsKnownFormat(TEXT("pmtiles")));
		TestFalse(TEXT("tiff not known"), FLogic::IsKnownFormat(TEXT("tiff")));
		TestFalse(TEXT("empty not known"), FLogic::IsKnownFormat(TEXT("")));

		FMantlePlaceVaultItem Base;
		Base.Formats = { TEXT("pmtiles"), TEXT("geotiff") };
		TestTrue(TEXT("Base bundle (no glb) is incomplete"), FLogic::IsIncompleteBundle(Base));

		FMantlePlaceVaultItem UpperGlb;
		UpperGlb.Formats = { TEXT("GLB") };
		TestFalse(TEXT("GLB (uppercase) counts as complete"), FLogic::IsIncompleteBundle(UpperGlb));

		FMantlePlaceVaultItem Marker;
		TestTrue(TEXT("Empty-formats bundle routes through materialize (incomplete)"),
			FLogic::IsIncompleteBundle(Marker));
	}

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

	{
		using EOutcome = EMantlePlaceMaterializeStartOutcome;
		auto StartOf = [](const TCHAR* Body, int32 Status) -> FMantlePlaceMaterializeStart
		{
			FMantlePlaceMaterializeStart Start;
			FString Error;
			if (FMantlePlaceVaultLogic::IsDeliveryStateUnknown(Status, Body))
			{
				Start.Outcome = EMantlePlaceMaterializeStartOutcome::DeliveryUnknown;
			}
			else
			{
				FMantlePlaceVaultLogic::ParseMaterializeStartResponse(Body, Start, Error);
			}
			return Start;
		};

		TestTrue(TEXT("503 delivery_state_unknown is the unproven answer"),
			FLogic::IsDeliveryStateUnknown(503, TEXT("{\"error\":\"unavailable\",\"code\":\"delivery_state_unknown\"}")));
		TestFalse(TEXT("A bare 503 is an outage, not the unproven answer"),
			FLogic::IsDeliveryStateUnknown(503, TEXT("{\"error\":\"unavailable\"}")));
		TestFalse(TEXT("A 503 whose body is not JSON is an outage"), FLogic::IsDeliveryStateUnknown(503, TEXT("<html>")));
		TestFalse(TEXT("The code under another status is not the unproven answer"),
			FLogic::IsDeliveryStateUnknown(500, TEXT("{\"code\":\"delivery_state_unknown\"}")));

		TestEqual(TEXT("The no-op is a complete order: fetch the view"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"noop\":true,\"delivered\":[\"unreal_heightmap\"]}"), 200)),
			EMantlePlaceImportRoute::DownloadView);
		TestEqual(TEXT("The unproven 503 is 'try the download', not a refusal"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"code\":\"delivery_state_unknown\"}"), 503)),
			EMantlePlaceImportRoute::DownloadView);
		TestEqual(TEXT("A fresh job is an on-demand order: the Prepare follows"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"jobId\":\"j1\",\"tokens\":[\"a\"]}"), 201)),
			EMantlePlaceImportRoute::Prepare);
		TestEqual(TEXT("A joined run is the Prepare's to follow"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"coalesced\":true,\"activeJobId\":\"j1\"}"), 200)),
			EMantlePlaceImportRoute::Prepare);
		TestEqual(TEXT("The single-flight 409 is a join: the Prepare follows"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"error\":\"busy\",\"code\":\"active_job\",\"activeJobId\":\"j1\"}"), 409)),
			EMantlePlaceImportRoute::Prepare);
		TestEqual(TEXT("A parked pick is the Prepare's"),
			FLogic::DecideImportRoute(StartOf(TEXT("{\"queued\":true,\"pendingTokens\":[\"a\"]}"), 202)),
			EMantlePlaceImportRoute::Prepare);

		const FString ViewBody = FLogic::BuildViewDownloadBody();
		TestEqual(TEXT("View request: format is the whole-archive token"),
			VaultReadStringField(ViewBody, TEXT("format")), FString(TEXT("bundle")));
		TestEqual(TEXT("View request, exact bytes"),
			ViewBody,
			FString(TEXT("{\"format\":\"bundle\",\"view\":{\"host\":\"unreal\",\"levels\":{\"*\":\"MAX\"}}}")));

		FMantlePlacePresignedDownload View;
		FString Error;
		TestTrue(TEXT("A view response parses"), FLogic::ParseDownloadResponse(
			TEXT("{\"url\":\"https://r2.example/v.zip\",\"expiresAt\":\"2026-10-26T00:00:00Z\",\"sizeBytes\":4194304,"
				"\"manifestSha256\":\"ab12\",\"view\":{\"partial\":false,\"parts\":[\"unreal_heightmap@MAX\",\"unreal_drape\"]}}"),
			View, Error));
		TestTrue(TEXT("The view block marks a view"), View.bIsView);
		TestFalse(TEXT("A complete view is not partial"), View.bPartial);
		TestEqual(TEXT("Parts are read"), View.Parts.Num(), 2);
		TestTrue(TEXT("The stated length is read"), View.bHasSizeBytes && View.SizeBytes == 4194304);
		TestEqual(TEXT("The manifest digest is read"), View.ManifestSha256, FString(TEXT("ab12")));

		FMantlePlacePresignedDownload Whole;
		TestTrue(TEXT("A whole-archive response parses"),
			FLogic::ParseDownloadResponse(TEXT("{\"url\":\"https://r2.example/w.zip\",\"expiresAt\":\"2026-10-26T00:00:00Z\"}"), Whole, Error));
		TestFalse(TEXT("No view block: the whole archive"), Whole.bIsView);
		TestFalse(TEXT("No stated length"), Whole.bHasSizeBytes);
		TestTrue(TEXT("No manifest digest"), Whole.ManifestSha256.IsEmpty());

		FMantlePlacePresignedDownload Partial;
		FLogic::ParseDownloadResponse(
			TEXT("{\"url\":\"https://r2.example/p.zip\",\"view\":{\"partial\":true,\"parts\":[]}}"), Partial, Error);
		TestTrue(TEXT("A partial view is read as partial"), Partial.bIsView && Partial.bPartial);

		const TArray<FString> NoneDelivered;
		const TArray<FString> AllOutside = { TEXT("flood.nfhl"), TEXT("cesium.terrain") };
		FMantlePlacePresignedDownload Full = View;
		Full.Parts.Add(TEXT("flood.nfhl@MAX"));
		Full.Parts.Add(TEXT("cesium.terrain"));
		TestEqual(TEXT("A complete view is downloaded"), FLogic::DecideViewFetch(Full, true, AllOutside), EMantlePlaceViewFetch::View);
		TestEqual(TEXT("A complete view proves a complete order even when the state was unknown"),
			FLogic::DecideViewFetch(Full, false, NoneDelivered), EMantlePlaceViewFetch::View);
		TestEqual(TEXT("A view of an order that has no outside file is enough"),
			FLogic::DecideViewFetch(View, true, NoneDelivered), EMantlePlaceViewFetch::View);
		TestEqual(TEXT("A view lacking a file the order has outside the block: the whole bundle instead"),
			FLogic::DecideViewFetch(View, true, AllOutside), EMantlePlaceViewFetch::WholeBundleInstead);
		TestEqual(TEXT("With the delivery state unknown every outside file is required of the view"),
			FLogic::DecideViewFetch(View, false, NoneDelivered), EMantlePlaceViewFetch::WholeBundleInstead);
		TestEqual(TEXT("A view lacking only the Cesium tileset the order has"),
			FLogic::MissingFromView(View.Parts, true, TArray<FString>{ TEXT("cesium.terrain") }).Num(), 1);
		TestEqual(TEXT("A partial view is never imported: the Prepare builds the rest"),
			FLogic::DecideViewFetch(Partial, true, AllOutside), EMantlePlaceViewFetch::Prepare);
		TestEqual(TEXT("A whole archive after a no-op is importable"),
			FLogic::DecideViewFetch(Whole, true, AllOutside), EMantlePlaceViewFetch::WholeArchive);
		TestEqual(TEXT("A whole archive after the unproven 503 may be an unbuilt on-demand bundle: Prepare"),
			FLogic::DecideViewFetch(Whole, false, NoneDelivered), EMantlePlaceViewFetch::Prepare);
	}

	return true;
}

#endif
