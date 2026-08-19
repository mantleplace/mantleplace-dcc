// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonWriter.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	/** Serialize a JSON object to a condensed (single-line) string suitable for a request body. */
	FString VaultSerializeCondensed(const TSharedRef<FJsonObject>& Root)
	{
		FString Out;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	/** Deserialize a JSON object; returns null on malformed input. */
	TSharedPtr<FJsonObject> VaultDeserializeObject(const FString& JsonStr)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
		if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
		{
			return Root;
		}
		return nullptr;
	}

	/** Parse one bundle object into Item. Returns false only on a missing required field ("id"). */
	bool ParseBundle(const TSharedRef<FJsonObject>& B, FMantlePlaceVaultItem& Item, FString& OutError)
	{
		// "id" is the download key - required; fail-closed if absent.
		if (!B->TryGetStringField(TEXT("id"), Item.OrderId) || Item.OrderId.IsEmpty())
		{
			OutError = TEXT("Vault bundle entry missing required 'id'");
			return false;
		}

		B->TryGetStringField(TEXT("aoiLabel"), Item.AoiLabel);
		B->TryGetStringField(TEXT("createdAt"), Item.CreatedAt);
		B->TryGetNumberField(TEXT("areaKm2"), Item.AreaKm2);

		FString StatusStr;
		B->TryGetStringField(TEXT("status"), StatusStr);
		Item.Status = FMantlePlaceVaultLogic::ParseStatus(StatusStr);

		// Sidecar fields - null/absent = "unknown" (legacy bundles), tracked by bHas* companions.
		const TSharedPtr<FJsonObject>* LayersObj = nullptr;
		if (B->TryGetObjectField(TEXT("layers"), LayersObj) && LayersObj != nullptr && LayersObj->IsValid())
		{
			Item.bLayersKnown = true;
			(*LayersObj)->TryGetBoolField(TEXT("imagery"), Item.Layers.bImagery);
			(*LayersObj)->TryGetBoolField(TEXT("basemap"), Item.Layers.bBasemap);
			(*LayersObj)->TryGetBoolField(TEXT("elevation"), Item.Layers.bElevation);
		}

		double ManifestVersion = 0.0;
		if (B->TryGetNumberField(TEXT("manifestVersion"), ManifestVersion))
		{
			Item.bHasManifestVersion = true;
			Item.ManifestVersion = static_cast<int32>(ManifestVersion);
		}

		double SizeBytes = 0.0;
		if (B->TryGetNumberField(TEXT("sizeBytes"), SizeBytes))
		{
			Item.bHasSizeBytes = true;
			Item.SizeBytes = static_cast<int64>(SizeBytes);
		}

		FString Sha256;
		if (B->TryGetStringField(TEXT("sha256"), Sha256) && !Sha256.IsEmpty())
		{
			Item.bHasSha256 = true;
			Item.Sha256 = MoveTemp(Sha256);
		}

		// formats: array of strings.
		const TArray<TSharedPtr<FJsonValue>>* FormatsArr = nullptr;
		if (B->TryGetArrayField(TEXT("formats"), FormatsArr))
		{
			for (const TSharedPtr<FJsonValue>& Value : *FormatsArr)
			{
				FString Fmt;
				if (Value.IsValid() && Value->TryGetString(Fmt))
				{
					Item.Formats.Add(MoveTemp(Fmt));
				}
			}
		}

		// download.formats: array of { format, byteSize }.
		const TSharedPtr<FJsonObject>* DownloadObj = nullptr;
		if (B->TryGetObjectField(TEXT("download"), DownloadObj) && DownloadObj != nullptr && DownloadObj->IsValid())
		{
			const TArray<TSharedPtr<FJsonValue>>* DownloadFormatsArr = nullptr;
			if ((*DownloadObj)->TryGetArrayField(TEXT("formats"), DownloadFormatsArr))
			{
				for (const TSharedPtr<FJsonValue>& Value : *DownloadFormatsArr)
				{
					const TSharedPtr<FJsonObject>* Obj = nullptr;
					if (Value.IsValid() && Value->TryGetObject(Obj) && Obj != nullptr && Obj->IsValid())
					{
						FMantlePlaceVaultArtifact Artifact;
						(*Obj)->TryGetStringField(TEXT("format"), Artifact.Format);
						double ByteSize = 0.0;
						(*Obj)->TryGetNumberField(TEXT("byteSize"), ByteSize);
						Artifact.ByteSize = static_cast<int64>(ByteSize);
						Item.DownloadFormats.Add(MoveTemp(Artifact));
					}
				}
			}
		}

		return true;
	}
}

FString FMantlePlaceVaultLogic::NormalizeBaseUrl(const FString& BaseUrl)
{
	FString Trimmed = BaseUrl.TrimStartAndEnd();
	while (Trimmed.EndsWith(TEXT("/")))
	{
		Trimmed.LeftChopInline(1);
	}
	return Trimmed;
}

FString FMantlePlaceVaultLogic::BuildListBundlesUrl(const FString& BaseUrl)
{
	return NormalizeBaseUrl(BaseUrl) + TEXT("/api/v1/vault/bundles");
}

FString FMantlePlaceVaultLogic::BuildDownloadUrl(const FString& BaseUrl, const FString& OrderId)
{
	// OrderId is a UUID (URL-safe), but encode defensively as a path segment.
	const FString EncodedId = FGenericPlatformHttp::UrlEncode(OrderId);
	return NormalizeBaseUrl(BaseUrl) + TEXT("/api/v1/vault/bundles/") + EncodedId + TEXT("/download");
}

FString FMantlePlaceVaultLogic::BuildDownloadBody(const FString& Format)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("format"), Format);
	return VaultSerializeCondensed(Root);
}

bool FMantlePlaceVaultLogic::ParseListResponse(const FString& JsonStr, TArray<FMantlePlaceVaultItem>& OutItems, FString& OutError, TArray<FString>* OutWarnings)
{
	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in vault list response");
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* BundlesArr = nullptr;
	if (!Root->TryGetArrayField(TEXT("bundles"), BundlesArr))
	{
		// Surface a platform error body if that's what we got instead.
		FString Code;
		if (!ParseErrorBody(JsonStr, OutError, Code))
		{
			OutError = TEXT("Vault list response missing 'bundles' array");
		}
		return false;
	}

	TArray<FMantlePlaceVaultItem> Items;
	Items.Reserve(BundlesArr->Num());
	for (const TSharedPtr<FJsonValue>& Value : *BundlesArr)
	{
		const TSharedPtr<FJsonObject>* Obj = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Obj) || Obj == nullptr || !Obj->IsValid())
		{
			// One malformed (non-object) entry must not blank the whole vault - skip it.
			if (OutWarnings != nullptr)
			{
				OutWarnings->Add(TEXT("Skipped a vault entry that is not a JSON object."));
			}
			continue;
		}

		FMantlePlaceVaultItem Item;
		FString ItemError;
		if (!ParseBundle(Obj->ToSharedRef(), Item, ItemError))
		{
			// A single unparseable bundle (e.g. missing id) is skipped, not fatal.
			if (OutWarnings != nullptr)
			{
				OutWarnings->Add(FString::Printf(TEXT("Skipped a vault entry: %s"), *ItemError));
			}
			continue;
		}
		Items.Add(MoveTemp(Item));
	}

	OutItems = MoveTemp(Items);
	return true;
}

bool FMantlePlaceVaultLogic::ParseDownloadResponse(const FString& JsonStr, FMantlePlacePresignedDownload& OutDownload, FString& OutError)
{
	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in download response");
		return false;
	}

	FMantlePlacePresignedDownload Parsed;
	if (!Root->TryGetStringField(TEXT("url"), Parsed.Url) || Parsed.Url.IsEmpty())
	{
		FString Code;
		if (!ParseErrorBody(JsonStr, OutError, Code))
		{
			OutError = TEXT("Download response missing 'url'");
		}
		return false;
	}

	Root->TryGetStringField(TEXT("expiresAt"), Parsed.ExpiresAt);
	OutDownload = MoveTemp(Parsed);
	return true;
}

bool FMantlePlaceVaultLogic::ParseErrorBody(const FString& JsonStr, FString& OutError, FString& OutCode)
{
	OutCode.Empty();

	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		return false;
	}

	// Optional machine-readable code (e.g. "refunded" / "revoked" on a 410). A separate read,
	// deliberately unaffected by the message precedence below.
	Root->TryGetStringField(TEXT("code"), OutCode);

	// ONE precedence order for every platform error-body parser, auth and vault alike (HPS-48):
	// most-specific human prose first, machine codes last. This parser and auth's
	// ParseErrorResponse shipped opposite orders once; the order is now corpus-pinned
	// (vault.errorBodyPrecedence).
	static const TCHAR* const Keys[] = {
		TEXT("error_description"),
		TEXT("msg"),
		TEXT("message"),
		TEXT("error_code"),
		TEXT("error")
	};

	for (const TCHAR* const Key : Keys)
	{
		FString Value;
		if (Root->TryGetStringField(Key, Value) && !Value.IsEmpty())
		{
			OutError = Value;
			return true;
		}
	}

	return false;
}

EMantlePlaceVaultBundleStatus FMantlePlaceVaultLogic::ParseStatus(const FString& Status)
{
	if (Status.Equals(TEXT("available"), ESearchCase::IgnoreCase))
	{
		return EMantlePlaceVaultBundleStatus::Available;
	}
	if (Status.Equals(TEXT("refresh-pending"), ESearchCase::IgnoreCase))
	{
		return EMantlePlaceVaultBundleStatus::RefreshPending;
	}
	if (Status.Equals(TEXT("refunded"), ESearchCase::IgnoreCase))
	{
		return EMantlePlaceVaultBundleStatus::Refunded;
	}
	if (Status.Equals(TEXT("failed"), ESearchCase::IgnoreCase))
	{
		return EMantlePlaceVaultBundleStatus::Failed;
	}
	return EMantlePlaceVaultBundleStatus::Unknown;
}

bool FMantlePlaceVaultLogic::IsDownloadable(const FMantlePlaceVaultItem& Item)
{
	return Item.Status == EMantlePlaceVaultBundleStatus::Available;
}

bool FMantlePlaceVaultLogic::IsKnownFormat(const FString& Format)
{
	for (const FString& Known : KnownFormats())
	{
		if (Format.Equals(Known, ESearchCase::IgnoreCase))
		{
			return true;
		}
	}
	return false;
}

const TArray<FString>& FMantlePlaceVaultLogic::KnownFormats()
{
	static const TArray<FString> Formats = {
		TEXT("glb"),
		TEXT("fbx"),
		TEXT("geotiff"),
		TEXT("cog"),
		TEXT("dwg"),
		TEXT("pmtiles")
	};
	return Formats;
}

FString FMantlePlaceVaultLogic::BuildMaterializeUrl(const FString& BaseUrl, const FString& OrderId)
{
	// OrderId is a UUID (URL-safe), but encode defensively as a path segment (mirrors BuildDownloadUrl).
	const FString EncodedId = FGenericPlatformHttp::UrlEncode(OrderId);
	return NormalizeBaseUrl(BaseUrl) + TEXT("/api/v1/vault/bundles/") + EncodedId + TEXT("/materialize");
}

bool FMantlePlaceVaultLogic::IsValidMaterializeScope(const FString& Scope)
{
	return Scope.Equals(TEXT("unreal"), ESearchCase::IgnoreCase)
		|| Scope.Equals(TEXT("all"), ESearchCase::IgnoreCase);
}

const TArray<FString>& FMantlePlaceVaultLogic::TargetedImportTokens()
{
	// The targeted layer set: every token this importer can land in the level today. Grows in
	// lockstep with the importer (raster layers join once the ETL ships their UE-ready variants).
	static const TArray<FString> Tokens = {
		TEXT("elevation.heightmap_png"),  // -> ALandscape
		TEXT("imagery.drape_png"),        // -> drape texture + material
		TEXT("mesh.glb"),                 // -> terrain static mesh (Nanite)
		TEXT("buildings.glb"),            // -> buildings static mesh
		TEXT("vector.geojson"),           // -> road spline actors (Vector/RoadSplines.geojson)
		TEXT("landcover.tree_points_csv") // -> tree-points DataTable (PCG scatter input)
	};
	return Tokens;
}

FString FMantlePlaceVaultLogic::BuildMaterializeBody(const FString& Scope)
{
	// Web contract: body { "tokens": "unreal" | "all" | PackagingFormatToken[] }. "all" passes
	// through as the keyword (the full ON_DEMAND set); everything else sends the explicit
	// targeted-token array so the layers this importer consumes are exactly what materializes.
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	if (Scope.Equals(TEXT("all"), ESearchCase::IgnoreCase))
	{
		Root->SetStringField(TEXT("tokens"), TEXT("all"));
	}
	else
	{
		TArray<TSharedPtr<FJsonValue>> Tokens;
		for (const FString& Token : TargetedImportTokens())
		{
			Tokens.Add(MakeShared<FJsonValueString>(Token));
		}
		Root->SetArrayField(TEXT("tokens"), Tokens);
	}
	return VaultSerializeCondensed(Root);
}

bool FMantlePlaceVaultLogic::ParseMaterializeStartResponse(const FString& JsonStr, FString& OutJobId, bool& bOutAlreadyRunning, FString& OutError)
{
	OutJobId.Empty();
	bOutAlreadyRunning = false;

	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in materialize response");
		return false;
	}

	FString JobId;
	if (Root->TryGetStringField(TEXT("jobId"), JobId) && !JobId.IsEmpty())
	{
		OutJobId = MoveTemp(JobId);
		bOutAlreadyRunning = false;
		return true;
	}

	// Single-flight 409: a materialize is already running for this order - poll the existing job.
	if (Root->TryGetStringField(TEXT("activeJobId"), JobId) && !JobId.IsEmpty())
	{
		OutJobId = MoveTemp(JobId);
		bOutAlreadyRunning = true;
		return true;
	}

	FString Code;
	if (!ParseErrorBody(JsonStr, OutError, Code))
	{
		OutError = TEXT("Materialize response missing 'jobId'");
	}
	return false;
}

EMantlePlaceMaterializeState FMantlePlaceVaultLogic::ParseMaterializeState(const FString& State)
{
	static const TArray<FString> PendingWords = { TEXT("pending"), TEXT("queued"), TEXT("accepted"), TEXT("waiting") };
	static const TArray<FString> ProcessingWords = { TEXT("processing"), TEXT("running"), TEXT("in_progress"), TEXT("in-progress"), TEXT("active"), TEXT("materializing"), TEXT("started") };
	static const TArray<FString> CompleteWords = { TEXT("complete"), TEXT("completed"), TEXT("ready"), TEXT("available"), TEXT("done"), TEXT("succeeded"), TEXT("success") };
	static const TArray<FString> FailedWords = { TEXT("failed"), TEXT("error"), TEXT("errored"), TEXT("failure") };

	auto Matches = [&State](const TArray<FString>& Bucket)
	{
		for (const FString& Word : Bucket)
		{
			if (State.Equals(Word, ESearchCase::IgnoreCase))
			{
				return true;
			}
		}
		return false;
	};

	if (Matches(PendingWords))    { return EMantlePlaceMaterializeState::Pending; }
	if (Matches(ProcessingWords)) { return EMantlePlaceMaterializeState::Processing; }
	if (Matches(CompleteWords))   { return EMantlePlaceMaterializeState::Complete; }
	if (Matches(FailedWords))     { return EMantlePlaceMaterializeState::Failed; }
	return EMantlePlaceMaterializeState::Unknown;
}

bool FMantlePlaceVaultLogic::ParseMaterializeStatus(const FString& JsonStr, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError)
{
	OutStatus = FMantlePlaceMaterializeStatus();

	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in materialize status response");
		return false;
	}

	// "status" is canonical; "state" is accepted as an alias.
	FString StateStr;
	if (!Root->TryGetStringField(TEXT("status"), StateStr) || StateStr.IsEmpty())
	{
		Root->TryGetStringField(TEXT("state"), StateStr);
	}

	if (StateStr.IsEmpty())
	{
		// No status field: surface a platform error envelope if that's what we got.
		FString Code;
		if (!ParseErrorBody(JsonStr, OutError, Code))
		{
			OutError = TEXT("Materialize status response missing 'status'");
		}
		return false;
	}

	OutStatus.State = ParseMaterializeState(StateStr);

	// progress/fraction in [0,1]; a value > 1 is treated as a percent and normalized. Absent -> -1
	// (indeterminate), the struct default.
	double Progress = 0.0;
	if (Root->TryGetNumberField(TEXT("progress"), Progress) || Root->TryGetNumberField(TEXT("fraction"), Progress))
	{
		if (Progress > 1.0)
		{
			Progress /= 100.0;
		}
		OutStatus.Fraction = FMath::Clamp(static_cast<float>(Progress), 0.0f, 1.0f);
	}

	Root->TryGetStringField(TEXT("message"), OutStatus.Message);
	Root->TryGetStringField(TEXT("jobId"), OutStatus.JobId);
	return true;
}

bool FMantlePlaceVaultLogic::IsIncompleteBundle(const FMantlePlaceVaultItem& Item)
{
	for (const FString& Format : Item.Formats)
	{
		if (Format.Equals(TEXT("glb"), ESearchCase::IgnoreCase))
		{
			return false; // ships a UE-importable terrain mesh -> already materialized
		}
	}
	// No glb terrain mesh advertised. That is either a BASE bundle (formats present, no glb) or the
	// base_on_demand marker with an empty formats list. Both need their Unreal formats
	// generated, so Import must route through materialize first rather than dead-ending at the
	// importer's manifest gate. Only a listing that explicitly advertises glb is treated as
	// confidently complete; anything else materializes (an already-materialized bundle coalesces to a
	// no-op job on the web side, so this is safe for the unknown/legacy case too).
	return true;
}

FString FMantlePlaceVaultLogic::DeriveTierLabel(const FMantlePlaceVaultItem& Item)
{
	if (Item.Formats.Num() == 0)
	{
		return TEXT("Unknown");
	}
	return IsIncompleteBundle(Item) ? TEXT("Base") : TEXT("Unreal");
}
