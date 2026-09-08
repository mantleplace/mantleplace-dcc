// Copyright Mantle Place. All Rights Reserved.

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
		const TCHAR* const KeyEntryCount = TEXT("entry_count");
		const TCHAR* const KeyScheme = TEXT("scheme_version");

		/** The root every staged bundle and its record live under. */
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
		// A record without the content it describes is bookkeeping about a directory somebody
		// cleaned out. The rewritten layer.json is the one file the stream cannot serve without.
		if (!bRecordFound || !bTerrainRootPresent)
		{
			return EVerdict::Stage;
		}

		// A layout this build does not know. Re-stage rather than serve a directory whose shape it
		// cannot predict — the same reasoning as the provenance record, minus the refusal, because
		// nothing here is a user's content to protect.
		if (Found.SchemeVersion != CurrentSchemeVersion)
		{
			return EVerdict::Stage;
		}

		// An extraction that wrote nothing is not a staged bundle, whatever else the record says.
		if (Found.EntryCount <= 0)
		{
			return EVerdict::Stage;
		}

		// Case-sensitive throughout, for the reason the provenance record gives: an order id and a
		// hex digest are both case-significant as delivered, and an equivalence nobody stated is not
		// ours to invent.
		if (!Found.Identity.Equals(Incoming.Identity, ESearchCase::CaseSensitive))
		{
			return EVerdict::Stage;
		}

		// The one that catches a re-materialized order. The identity is stable across rebuilds by
		// design, so without this a rebuilt bundle would be served from the previous build's tiles.
		if (!Found.ManifestSha256.Equals(Incoming.ManifestSha256, ESearchCase::CaseSensitive)
			|| Found.ManifestSha256.IsEmpty())
		{
			return EVerdict::Stage;
		}

		// The bundle layout renames its terrain folder across versions, and the manifest pointer is
		// what every served URL is built from. Either changing means the directory on disk is not
		// the one this stream would produce.
		if (!Found.TerrainPrefix.Equals(Incoming.TerrainPrefix, ESearchCase::CaseSensitive)
			|| !Found.CesiumTerrainPath.Equals(Incoming.CesiumTerrainPath, ESearchCase::CaseSensitive))
		{
			return EVerdict::Stage;
		}

		return EVerdict::Reuse;
	}

	FString ToJson(const FRecord& Record)
	{
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(KeyIdentity, Record.Identity);
		Root->SetStringField(KeyManifestSha256, Record.ManifestSha256);
		Root->SetStringField(KeyTerrainPrefix, Record.TerrainPrefix);
		Root->SetStringField(KeyCesiumTerrainPath, Record.CesiumTerrainPath);
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
			// A record that does not say which bundle it describes cannot answer the only question
			// it exists to answer, so it is not a record. Treated as absent, which re-stages.
			return false;
		}
		Root->TryGetStringField(KeyManifestSha256, Parsed.ManifestSha256);
		Root->TryGetStringField(KeyTerrainPrefix, Parsed.TerrainPrefix);
		Root->TryGetStringField(KeyCesiumTerrainPath, Parsed.CesiumTerrainPath);
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
		// The FULL identity, unlike the content folder's truncation: nothing reads this path, so
		// there is no readability to trade for, and the availability rewrite declares every tile it
		// finds beneath here — a collision would serve another bundle's tiles as this one's.
		return StagingRoot() / Identity;
	}

	FString RecordPath(const FString& Identity)
	{
		// A sibling of the directory, not a file inside it: the directory is served over HTTP.
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
