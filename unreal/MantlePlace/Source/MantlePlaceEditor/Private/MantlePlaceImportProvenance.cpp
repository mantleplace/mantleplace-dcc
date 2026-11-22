// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

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
		const TCHAR* const KeyBbox = TEXT("bbox");
		const TCHAR* const KeyWest = TEXT("west");
		const TCHAR* const KeySouth = TEXT("south");
		const TCHAR* const KeyEast = TEXT("east");
		const TCHAR* const KeyNorth = TEXT("north");
	}

	EVerdict Classify(
		bool bContentFolderExists,
		bool bRecordFound,
		const FRecord& Found,
		const FString& IncomingIdentity)
	{
		if (!bContentFolderExists)
		{
			return EVerdict::NoPriorImport;
		}

		if (!bRecordFound)
		{
			return EVerdict::LegacyUnrecorded;
		}

		if (Found.SchemeVersion > CurrentSchemeVersion)
		{
			return EVerdict::NewerScheme;
		}

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
		if (Record.bHasExtent && Record.Extent.IsValid())
		{
			const TSharedRef<FJsonObject> Bbox = MakeShared<FJsonObject>();
			Bbox->SetNumberField(KeyWest, Record.Extent.West);
			Bbox->SetNumberField(KeySouth, Record.Extent.South);
			Bbox->SetNumberField(KeyEast, Record.Extent.East);
			Bbox->SetNumberField(KeyNorth, Record.Extent.North);
			Root->SetObjectField(KeyBbox, Bbox);
		}

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
			return false;
		}
		int32 Scheme = 0;
		if (Root->TryGetNumberField(KeyScheme, Scheme))
		{
			Parsed.SchemeVersion = Scheme;
		}
		Root->TryGetStringField(KeyContentRoot, Parsed.ContentRoot);
		Root->TryGetStringField(KeyJobId, Parsed.JobId);

		const TSharedPtr<FJsonObject>* BboxObject = nullptr;
		if (Root->TryGetObjectField(KeyBbox, BboxObject) && BboxObject != nullptr && BboxObject->IsValid())
		{
			FMantlePlaceExtent Extent;
			if ((*BboxObject)->TryGetNumberField(KeyWest, Extent.West)
				&& (*BboxObject)->TryGetNumberField(KeySouth, Extent.South)
				&& (*BboxObject)->TryGetNumberField(KeyEast, Extent.East)
				&& (*BboxObject)->TryGetNumberField(KeyNorth, Extent.North)
				&& Extent.IsValid())
			{
				Parsed.bHasExtent = true;
				Parsed.Extent = Extent;
			}
		}

		OutRecord = MoveTemp(Parsed);
		return true;
	}

	FString RecordPath(const FString& Identity)
	{
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
