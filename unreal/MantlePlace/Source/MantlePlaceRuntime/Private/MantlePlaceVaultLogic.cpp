// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "Policies/CondensedJsonPrintPolicy.h" // TCondensedJsonPrintPolicy: not transitively available in a game target
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

		// The vault LIST contract carries `manifestVersion` for each bundle at rest, and the vault
		// spans both eras: a bundle cut before the MPB re-baseline reports the integer 19, one cut
		// after reports the string "1.0.0". Both shapes are read because both are live facts about
		// the listing, and the value is SURFACED rather than gated on — the import gate is the one
		// place a version decides anything. Absent stays absent (HPS-20: unknown is not zero).
		if (!B->TryGetStringField(TEXT("manifestVersion"), Item.ManifestVersion))
		{
			double NumericManifestVersion = 0.0;
			if (B->TryGetNumberField(TEXT("manifestVersion"), NumericManifestVersion))
			{
				Item.ManifestVersion = FString::FromInt(static_cast<int32>(NumericManifestVersion));
			}
		}
		Item.bHasManifestVersion = !Item.ManifestVersion.IsEmpty();

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

const FString& FMantlePlaceVaultLogic::WholeBundleFormat()
{
	static const FString Format = TEXT("bundle");
	return Format;
}

bool FMantlePlaceVaultLogic::IsPresignableFormat(const FString& Format)
{
	return IsKnownFormat(Format) || Format.Equals(WholeBundleFormat(), ESearchCase::IgnoreCase);
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

namespace
{
/** Read a string array field, skipping non-strings. Absent or wrong-typed yields an empty array. */
TArray<FString> VaultStringArray(const TSharedPtr<FJsonObject>& Root, const TCHAR* Field)
{
	TArray<FString> Values;
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (Root.IsValid() && Root->TryGetArrayField(Field, Array) && Array != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Entry : *Array)
		{
			FString Text;
			if (Entry.IsValid() && Entry->TryGetString(Text) && !Text.IsEmpty())
			{
				Values.Add(MoveTemp(Text));
			}
		}
	}
	return Values;
}

/** As above, for an array hanging off a nested object. */
TArray<FString> VaultStringArray(const TSharedPtr<FJsonObject>* Object, const TCHAR* Field)
{
	return (Object != nullptr && Object->IsValid()) ? VaultStringArray(*Object, Field) : TArray<FString>();
}

/** True when any element of Tokens is in Outstanding. */
bool VaultTouchesAny(const TArray<FString>& Tokens, const TArray<FString>& Outstanding)
{
	for (const FString& Token : Tokens)
	{
		if (Outstanding.Contains(Token))
		{
			return true;
		}
	}
	return false;
}
}

bool FMantlePlaceVaultLogic::IsPermanentlyAbsentReason(const FString& Reason)
{
	// Mirrors the platform's own non-retryable set exactly. Treating one of these as outstanding
	// makes a bundle that is as complete as it will ever be poll for its whole budget and then time
	// out, having been finished the entire time.
	return Reason.Equals(TEXT("no_features_in_aoi"), ESearchCase::IgnoreCase) || Reason.Equals(TEXT("area_cap_exceeded"), ESearchCase::IgnoreCase) || Reason.Equals(TEXT("outside_coverage"), ESearchCase::IgnoreCase);
}

TArray<FString> FMantlePlaceVaultLogic::RequestedForPolling(const FMantlePlaceMaterializeStart& Start)
{
	return Start.Tokens.Num() > 0 ? Start.Tokens : TargetedImportTokens();
}

bool FMantlePlaceVaultLogic::ParseMaterializeStartResponse(const FString& JsonStr, FMantlePlaceMaterializeStart& OutStart, FString& OutError)
{
	OutStart = FMantlePlaceMaterializeStart();

	const TSharedPtr<FJsonObject> Root = VaultDeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in materialize response");
		return false;
	}

	// Nothing to build: everything asked for is already delivered, or the rest can never be produced
	// for this area. A SUCCESS that names no job - the caller skips polling and downloads.
	bool bFlag = false;
	if (Root->TryGetBoolField(TEXT("noop"), bFlag) && bFlag)
	{
		OutStart.Outcome = EMantlePlaceMaterializeStartOutcome::NothingToDo;
		OutStart.Tokens = VaultStringArray(Root, TEXT("delivered"));
		return true;
	}

	// The order's core build has not finished; the picks are parked and fire on their own.
	if (Root->TryGetBoolField(TEXT("queued"), bFlag) && bFlag)
	{
		OutStart.Outcome = EMantlePlaceMaterializeStartOutcome::Queued;
		OutStart.Tokens = VaultStringArray(Root, TEXT("pendingTokens"));
		return true;
	}

	FString ActiveJobId;
	Root->TryGetStringField(TEXT("activeJobId"), ActiveJobId);

	bool bCoalesced = false;
	Root->TryGetBoolField(TEXT("coalesced"), bCoalesced);

	FString Code;
	Root->TryGetStringField(TEXT("code"), Code);

	// A run is already in flight. Checked before the error body because the single-flight response
	// carries both a job fact and error-ish prose, and the job fact is the useful half. An EMPTY id
	// still joins: polling is keyed on the order, not the job.
	if (!ActiveJobId.IsEmpty() || bCoalesced || Code.Equals(TEXT("active_job"), ESearchCase::CaseSensitive))
	{
		OutStart.Outcome = EMantlePlaceMaterializeStartOutcome::Joined;
		OutStart.JobId = MoveTemp(ActiveJobId);
		OutStart.Tokens = VaultStringArray(Root, TEXT("tokens"));
		return true;
	}

	FString ErrorCode;
	if (ParseErrorBody(JsonStr, OutError, ErrorCode))
	{
		return false;
	}

	FString JobId;
	if (Root->TryGetStringField(TEXT("jobId"), JobId) && !JobId.IsEmpty())
	{
		OutStart.Outcome = EMantlePlaceMaterializeStartOutcome::Started;
		OutStart.JobId = MoveTemp(JobId);
		OutStart.Tokens = VaultStringArray(Root, TEXT("tokens"));
		return true;
	}

	OutError = TEXT("Materialize response missing 'jobId'");
	return false;
}

bool FMantlePlaceVaultLogic::ParseMaterializeStartResponse(const FString& JsonStr, FString& OutJobId, bool& bOutAlreadyRunning, FString& OutError)
{
	FMantlePlaceMaterializeStart Start;
	const bool bParsed = ParseMaterializeStartResponse(JsonStr, Start, OutError);

	OutJobId = Start.JobId;
	bOutAlreadyRunning = Start.IsAlreadyRunning();
	return bParsed;
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
	return ParseMaterializeStatus(JsonStr, TArray<FString>(), OutStatus, OutError);
}

bool FMantlePlaceVaultLogic::ParseMaterializeStatus(const FString& JsonStr, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError)
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
		// `delivered` is the discriminator: the delivery-state document always declares it, where
		// `activeJob` is legitimately null on an idle order and every other field is optional.
		// Keying on an optional field would misread an idle bundle as a foreign shape.
		const TArray<TSharedPtr<FJsonValue>>* DeliveredArray = nullptr;
		if (Root->TryGetArrayField(TEXT("delivered"), DeliveredArray))
		{
			DeriveMaterializeDelivery(Root, Requested, OutStatus);
			return true;
		}

		// Neither shape: surface a platform error envelope if that's what we got.
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

void FMantlePlaceVaultLogic::DeriveMaterializeDelivery(const TSharedPtr<FJsonObject>& Root, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus)
{
	const TArray<FString> DeliveredAll = VaultStringArray(Root, TEXT("delivered"));

	TArray<FString> Blocked;
	const TArray<TSharedPtr<FJsonValue>>* NotDelivered = nullptr;
	if (Root->TryGetArrayField(TEXT("notDelivered"), NotDelivered) && NotDelivered != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Entry : *NotDelivered)
		{
			const TSharedPtr<FJsonObject>* Row = nullptr;
			if (!Entry.IsValid() || !Entry->TryGetObject(Row) || Row == nullptr || !Row->IsValid())
			{
				continue;
			}

			FString Token;
			FString Reason;
			(*Row)->TryGetStringField(TEXT("token"), Token);
			(*Row)->TryGetStringField(TEXT("reason"), Reason);

			if (!Token.IsEmpty() && Requested.Contains(Token) && IsPermanentlyAbsentReason(Reason))
			{
				FMantlePlaceMissingDeliverable Missing;
				Missing.Token = Token;
				Missing.Reason = Reason;
				OutStatus.Unproducible.Add(MoveTemp(Missing));
				Blocked.Add(Token);
			}
		}
	}

	TArray<FString> Outstanding;
	for (const FString& Token : Requested)
	{
		if (DeliveredAll.Contains(Token))
		{
			OutStatus.Delivered.Add(Token);
		}
		else if (!Blocked.Contains(Token))
		{
			Outstanding.Add(Token);
		}
	}

	const float Fraction = Requested.Num() == 0
	                           ? -1.0f
	                           : static_cast<float>(OutStatus.Delivered.Num()) / static_cast<float>(Requested.Num());

	// STOP: no yardstick, no verdict. "Nothing is outstanding" is vacuously true against an empty
	// requested set, so falling through would report Complete for a bundle nobody asked anything of.
	if (Requested.Num() == 0)
	{
		OutStatus.State = EMantlePlaceMaterializeState::Unknown;
		OutStatus.Message = TEXT("No deliverables were named, so there is nothing to check against.");
		return;
	}

	bool bStateUnknown = false;
	if (Root->TryGetBoolField(TEXT("deliveryStateUnknown"), bStateUnknown) && bStateUnknown)
	{
		OutStatus.State = EMantlePlaceMaterializeState::Unknown;
		OutStatus.Message = TEXT("The platform could not confirm what this bundle already has. Still checking...");
		return;
	}

	if (Outstanding.Num() == 0)
	{
		OutStatus.State = EMantlePlaceMaterializeState::Complete;
		OutStatus.Fraction = 1.0f;
		return;
	}

	const TSharedPtr<FJsonObject>* ActiveJob = nullptr;
	if (Root->TryGetObjectField(TEXT("activeJob"), ActiveJob) && ActiveJob != nullptr && ActiveJob->IsValid())
	{
		const int32 Building = VaultStringArray(ActiveJob, TEXT("tokens")).Num();
		OutStatus.State = EMantlePlaceMaterializeState::Processing;
		OutStatus.Fraction = Fraction;
		(*ActiveJob)->TryGetStringField(TEXT("id"), OutStatus.JobId);
		OutStatus.Message = Building > 0
		                        ? FString::Printf(TEXT("Building %d deliverable(s)..."), Building)
		                        : TEXT("Building...");
		return;
	}

	const TSharedPtr<FJsonObject>* LastAttempt = nullptr;
	if (Root->TryGetObjectField(TEXT("lastAttempt"), LastAttempt) && LastAttempt != nullptr && LastAttempt->IsValid() && VaultTouchesAny(VaultStringArray(LastAttempt, TEXT("tokens")), Outstanding))
	{
		FString Outcome;
		(*LastAttempt)->TryGetStringField(TEXT("outcome"), Outcome);

		OutStatus.State = EMantlePlaceMaterializeState::Failed;
		if (Outcome.Equals(TEXT("completed"), ESearchCase::IgnoreCase))
		{
			// STOP: the soft-fail envelope. The run says it finished, and produced none of it.
			OutStatus.Message = TEXT("The platform reported the job finished but produced none of these ")
			    TEXT("deliverables. Try generating again; if it repeats, the platform could not build them.");
		}
		else if (Outcome.Equals(TEXT("cancelled"), ESearchCase::IgnoreCase))
		{
			OutStatus.Message = TEXT("The platform's generation timed out and was swept. Try generating again.");
		}
		else
		{
			OutStatus.Message = TEXT("The platform could not build this bundle.");
		}
		return;
	}

	const TSharedPtr<FJsonObject>* LastFailed = nullptr;
	if (Root->TryGetObjectField(TEXT("lastFailed"), LastFailed) && LastFailed != nullptr && LastFailed->IsValid() && VaultTouchesAny(VaultStringArray(LastFailed, TEXT("tokens")), Outstanding))
	{
		OutStatus.State = EMantlePlaceMaterializeState::Failed;
		OutStatus.Message = TEXT("The platform could not build this bundle.");
		return;
	}

	OutStatus.State = EMantlePlaceMaterializeState::Pending;
	OutStatus.Fraction = Fraction;
	OutStatus.Message = TEXT("Waiting for the platform to pick this up...");
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
	// confidently complete; anything else materializes.
	//
	// That is safe for the unknown/legacy case ONLY because the NothingToDo outcome is handled: an
	// already-materialized bundle answers with {"noop":true,..} and NO job. This comment used to
	// claim the platform "coalesces to a no-op job", and it does not - there is no job. Believing
	// there was is what left the caller polling for a job that never existed.
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
