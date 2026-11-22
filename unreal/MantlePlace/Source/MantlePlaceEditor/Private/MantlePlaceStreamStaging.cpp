// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceStreamStaging.h"

#include "Dom/JsonObject.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace MantlePlaceStreamStaging
{
	namespace
	{
		const TCHAR* const KeyIdentity = TEXT("identity");
		const TCHAR* const KeyManifestSha256 = TEXT("manifest_sha256");
		const TCHAR* const KeyTerrainPrefix = TEXT("terrain_prefix");
		const TCHAR* const KeyCesiumTerrainPath = TEXT("cesium_terrain_path");
		const TCHAR* const KeyDrapePath = TEXT("drape_path");
		const TCHAR* const KeyEntryCount = TEXT("entry_count");
		const TCHAR* const KeyScheme = TEXT("scheme_version");

		FString StagingRoot()
		{
			return FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("StreamStaging");
		}
	}

	EVerdict Classify(
		bool bRecordFound,
		const FRecord& Found,
		const FRecord& Incoming,
		bool bTerrainRootPresent)
	{
		if (!bRecordFound || !bTerrainRootPresent)
		{
			return EVerdict::Stage;
		}

		if (Found.SchemeVersion != CurrentSchemeVersion)
		{
			return EVerdict::Stage;
		}

		if (Found.EntryCount <= 0)
		{
			return EVerdict::Stage;
		}

		if (!Found.Identity.Equals(Incoming.Identity, ESearchCase::CaseSensitive))
		{
			return EVerdict::Stage;
		}

		if (!Found.ManifestSha256.Equals(Incoming.ManifestSha256, ESearchCase::CaseSensitive)
			|| Found.ManifestSha256.IsEmpty())
		{
			return EVerdict::Stage;
		}

		if (!Found.TerrainPrefix.Equals(Incoming.TerrainPrefix, ESearchCase::CaseSensitive)
			|| !Found.CesiumTerrainPath.Equals(Incoming.CesiumTerrainPath, ESearchCase::CaseSensitive))
		{
			return EVerdict::Stage;
		}

		if (!Found.DrapePath.Equals(Incoming.DrapePath, ESearchCase::CaseSensitive))
		{
			return EVerdict::Stage;
		}

		return EVerdict::Reuse;
	}

	bool SelectsEntry(const FRecord& Record, const FString& EntryName)
	{
		if (EntryName.IsEmpty() || EntryName.EndsWith(TEXT("/")))
		{
			return false;
		}
		if (!Record.TerrainPrefix.IsEmpty() && EntryName.StartsWith(Record.TerrainPrefix, ESearchCase::IgnoreCase))
		{
			return true;
		}
		return !Record.DrapePath.IsEmpty() && EntryName.Equals(Record.DrapePath, ESearchCase::IgnoreCase);
	}

	FString ToJson(const FRecord& Record)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(KeyIdentity, Record.Identity);
		Root->SetStringField(KeyManifestSha256, Record.ManifestSha256);
		Root->SetStringField(KeyTerrainPrefix, Record.TerrainPrefix);
		Root->SetStringField(KeyCesiumTerrainPath, Record.CesiumTerrainPath);
		Root->SetStringField(KeyDrapePath, Record.DrapePath);
		Root->SetNumberField(KeyEntryCount, Record.EntryCount);
		Root->SetNumberField(KeyScheme, Record.SchemeVersion);

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
		Root->TryGetStringField(KeyManifestSha256, Parsed.ManifestSha256);
		Root->TryGetStringField(KeyTerrainPrefix, Parsed.TerrainPrefix);
		Root->TryGetStringField(KeyCesiumTerrainPath, Parsed.CesiumTerrainPath);
		Root->TryGetStringField(KeyDrapePath, Parsed.DrapePath);
		int32 EntryCount = 0;
		if (Root->TryGetNumberField(KeyEntryCount, EntryCount))
		{
			Parsed.EntryCount = EntryCount;
		}
		int32 Scheme = 0;
		if (Root->TryGetNumberField(KeyScheme, Scheme))
		{
			Parsed.SchemeVersion = Scheme;
		}

		OutRecord = MoveTemp(Parsed);
		return true;
	}

	FString StagingDir(const FString& Identity)
	{
		return StagingRoot() / Identity;
	}

	FString RecordPath(const FString& Identity)
	{
		return StagingRoot() / (Identity + TEXT(".json"));
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
