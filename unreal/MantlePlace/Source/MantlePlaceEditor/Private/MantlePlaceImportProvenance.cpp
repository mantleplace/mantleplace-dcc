// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportProvenance.h"

#include "MantlePlaceImportNaming.h"

#include "Dom/JsonObject.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace MantlePlaceImportProvenance
{
	namespace
	{
		const TCHAR* const KeyIdentity = TEXT("identity");
		const TCHAR* const KeyScheme = TEXT("scheme_version");
		const TCHAR* const KeyContentRoot = TEXT("content_root");
		const TCHAR* const KeyJobId = TEXT("job_id");
	}

	EVerdict Classify(
		bool bContentFolderExists,
		bool bRecordFound,
		const FRecord& Found,
		const FString& IncomingIdentity)
	{
		// No content means nothing to protect, whatever a leftover record says. A user who deleted
		// the folder by hand meant for the next import to be a fresh one.
		if (!bContentFolderExists)
		{
			return EVerdict::NoPriorImport;
		}

		// Content with no record predates the record. Refuse rather than adopt it: the folder was
		// keyed on a job id, so it is content for some build of some order, and this importer
		// cannot tell whether it is a build of THIS order without the record it does not have.
		if (!bRecordFound)
		{
			return EVerdict::LegacyUnrecorded;
		}

		// A record from a scheme this build does not know describes a layout it cannot predict, so
		// it cannot know what deleting would destroy.
		if (Found.SchemeVersion > CurrentSchemeVersion)
		{
			return EVerdict::NewerScheme;
		}

		// The comparison the truncation makes necessary. Case-sensitive: an order id and a hex
		// digest are both case-significant as delivered, and treating two identities that differ
		// only in case as the same one would be inventing an equivalence nobody stated.
		if (Found.Identity.Equals(IncomingIdentity, ESearchCase::CaseSensitive))
		{
			return EVerdict::SameIdentity;
		}

		return EVerdict::DifferentIdentity;
	}

	FString ExplainRefusal(EVerdict Verdict, const FRecord& Found, const FString& ContentPath)
	{
		switch (Verdict)
		{
		case EVerdict::NoPriorImport:
		case EVerdict::SameIdentity:
			return FString();

		case EVerdict::DifferentIdentity:
			return FString::Printf(
				TEXT("%s already holds an import of a different order (%s). Two identities that "
					 "share their first eight characters land in the same folder, and continuing "
					 "would delete that import. Move or delete it and try again."),
				*ContentPath, *MantlePlaceImportNaming::ShortIdentity(Found.Identity));

		case EVerdict::LegacyUnrecorded:
			return FString::Printf(
				TEXT("%s holds content from Mantle Place 0.3.0 or earlier, which was keyed on the "
					 "build rather than the order and cannot be matched to this bundle. Nothing "
					 "has been changed. Delete that folder (and the MP_* actors in your level "
					 "referencing it) and import again."),
				*ContentPath);

		case EVerdict::NewerScheme:
			return FString::Printf(
				TEXT("%s was written by a newer version of the Mantle Place plugin (layout %d; this "
					 "build understands %d) and its contents are not safe for this build to "
					 "replace. Update the plugin, or import to a different content root."),
				*ContentPath, Found.SchemeVersion, CurrentSchemeVersion);
		}
		return FString();
	}

	FString ToJson(const FRecord& Record)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(KeyIdentity, Record.Identity);
		Root->SetNumberField(KeyScheme, Record.SchemeVersion);
		Root->SetStringField(KeyContentRoot, Record.ContentRoot);
		Root->SetStringField(KeyJobId, Record.JobId);

		FString Out;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	bool FromJson(const FString& Json, FRecord& OutRecord)
	{
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
		TSharedPtr<FJsonObject> Root;
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			return false;
		}

		FRecord Parsed;
		if (!Root->TryGetStringField(KeyIdentity, Parsed.Identity) || Parsed.Identity.IsEmpty())
		{
			// A record with no identity cannot answer the only question it exists to answer, so it
			// is not a record. Treated as absent, which refuses rather than deletes.
			return false;
		}
		int32 Scheme = 0;
		if (Root->TryGetNumberField(KeyScheme, Scheme))
		{
			Parsed.SchemeVersion = Scheme;
		}
		Root->TryGetStringField(KeyContentRoot, Parsed.ContentRoot);
		Root->TryGetStringField(KeyJobId, Parsed.JobId);

		OutRecord = MoveTemp(Parsed);
		return true;
	}

	FString RecordPath(const FString& Identity)
	{
		// Keyed on the SHORT identity, deliberately: the folder is named by the short form, so the
		// record has to be findable from the folder. Its contents are what disambiguate.
		return FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("Imports")
			/ (MantlePlaceImportNaming::ShortIdentity(Identity) + TEXT(".json"));
	}

	bool Read(const FString& Identity, FRecord& OutRecord)
	{
		FString Json;
		if (!FFileHelper::LoadFileToString(Json, *RecordPath(Identity)))
		{
			return false;
		}
		return FromJson(Json, OutRecord);
	}

	bool Write(const FRecord& Record)
	{
		return FFileHelper::SaveStringToFile(ToJson(Record), *RecordPath(Record.Identity));
	}
}
