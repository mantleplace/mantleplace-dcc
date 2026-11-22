// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceAuthLogic.h"

#include "MantlePlaceSha256.h"

#include "Dom/JsonObject.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonWriter.h"
#include "Serialization/JsonSerializer.h"
#include "Misc/Base64.h"

namespace
{
	FString SerializeCondensed(const TSharedRef<FJsonObject>& Root)
	{
		FString Out;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	TSharedPtr<FJsonObject> DeserializeObject(const FString& JsonStr)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
		if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
		{
			return Root;
		}
		return nullptr;
	}

	int32 HexValue(TCHAR Ch)
	{
		if (Ch >= TEXT('0') && Ch <= TEXT('9')) return Ch - TEXT('0');
		if (Ch >= TEXT('a') && Ch <= TEXT('f')) return 10 + (Ch - TEXT('a'));
		if (Ch >= TEXT('A') && Ch <= TEXT('F')) return 10 + (Ch - TEXT('A'));
		return -1;
	}

	FString PercentEncode(const FString& In)
	{
		const FTCHARToUTF8 Utf8(*In);
		const uint8* Bytes = reinterpret_cast<const uint8*>(Utf8.Get());
		const int32 Len = Utf8.Length();
		static const TCHAR* const Hex = TEXT("0123456789ABCDEF");

		FString Out;
		Out.Reserve(Len * 3);
		for (int32 i = 0; i < Len; ++i)
		{
			const uint8 C = Bytes[i];
			const bool bUnreserved =
				(C >= 'A' && C <= 'Z') || (C >= 'a' && C <= 'z') || (C >= '0' && C <= '9') ||
				C == '-' || C == '.' || C == '_' || C == '~';
			if (bUnreserved)
			{
				Out.AppendChar(static_cast<TCHAR>(C));
			}
			else
			{
				Out.AppendChar(TEXT('%'));
				Out.AppendChar(Hex[(C >> 4) & 0xF]);
				Out.AppendChar(Hex[C & 0xF]);
			}
		}
		return Out;
	}

	FString PercentDecode(const FString& In)
	{
		TArray<uint8> Bytes;
		Bytes.Reserve(In.Len());
		for (int32 i = 0; i < In.Len(); ++i)
		{
			const TCHAR Ch = In[i];
			if (Ch == TEXT('+'))
			{
				Bytes.Add(static_cast<uint8>(' '));
			}
			else if (Ch == TEXT('%') && i + 2 < In.Len() && HexValue(In[i + 1]) >= 0 && HexValue(In[i + 2]) >= 0)
			{
				Bytes.Add(static_cast<uint8>((HexValue(In[i + 1]) << 4) | HexValue(In[i + 2])));
				i += 2;
			}
			else
			{
				Bytes.Add(static_cast<uint8>(Ch));
			}
		}
		Bytes.Add(0);
		return FString(UTF8_TO_TCHAR(reinterpret_cast<const ANSICHAR*>(Bytes.GetData())));
	}
}

FString FMantlePlaceAuthLogic::NormalizeBaseUrl(const FString& BaseUrl)
{
	FString Trimmed = BaseUrl.TrimStartAndEnd();
	while (Trimmed.EndsWith(TEXT("/")))
	{
		Trimmed.LeftChopInline(1);
	}
	return Trimmed;
}

bool FMantlePlaceAuthLogic::IsValidBaseUrl(const FString& BaseUrl)
{
	const FString Trimmed = BaseUrl.TrimStartAndEnd();

	FString Rest;
	if (Trimmed.StartsWith(TEXT("https://"), ESearchCase::IgnoreCase))
	{
		Rest = Trimmed.RightChop(FCString::Strlen(TEXT("https://")));
	}
	else if (Trimmed.StartsWith(TEXT("http://"), ESearchCase::IgnoreCase))
	{
		Rest = Trimmed.RightChop(FCString::Strlen(TEXT("http://")));
	}
	else
	{
		return false;
	}

	int32 SlashIdx = INDEX_NONE;
	const FString Host = Rest.FindChar(TEXT('/'), SlashIdx) ? Rest.Left(SlashIdx) : Rest;
	return !Host.IsEmpty();
}

FString FMantlePlaceAuthLogic::BuildRefreshGrantBody(const FString& RefreshToken)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("refresh_token"), RefreshToken);
	return SerializeCondensed(Root);
}

bool FMantlePlaceAuthLogic::ParseTokenResponse(const FString& JsonStr, FMantlePlaceAuthTokens& OutTokens, FString& OutError)
{
	const TSharedPtr<FJsonObject> Root = DeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in token response");
		return false;
	}

	FMantlePlaceAuthTokens Parsed;
	if (!Root->TryGetStringField(TEXT("access_token"), Parsed.AccessToken) || Parsed.AccessToken.IsEmpty())
	{
		if (!ParseErrorResponse(JsonStr, OutError))
		{
			OutError = TEXT("Token response missing access_token");
		}
		return false;
	}

	Root->TryGetStringField(TEXT("refresh_token"), Parsed.RefreshToken);

	double ExpiresIn = 0.0;
	if (Root->TryGetNumberField(TEXT("expires_in"), ExpiresIn))
	{
		Parsed.ExpiresInSeconds = static_cast<int32>(ExpiresIn);
	}

	if (Parsed.ExpiresInSeconds <= 0)
	{
		Parsed.ExpiresInSeconds = DefaultAccessTokenLifetimeSeconds;
	}

	const TSharedPtr<FJsonObject>* UserObj = nullptr;
	if (Root->TryGetObjectField(TEXT("user"), UserObj) && UserObj != nullptr && UserObj->IsValid())
	{
		(*UserObj)->TryGetStringField(TEXT("id"), Parsed.UserId);
	}

	OutTokens = MoveTemp(Parsed);
	return true;
}

bool FMantlePlaceAuthLogic::ParseErrorResponse(const FString& JsonStr, FString& OutError)
{
	const TSharedPtr<FJsonObject> Root = DeserializeObject(JsonStr);
	if (!Root.IsValid())
	{
		return false;
	}

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

bool FMantlePlaceAuthLogic::IsDefinitiveRejection(int32 HttpStatus, const FString& Body)
{
	if (HttpStatus != 400 && HttpStatus != 401)
	{
		return false;
	}

	const TSharedPtr<FJsonObject> Root = DeserializeObject(Body);
	if (!Root.IsValid())
	{
		return false;
	}

	static const TCHAR* const CodeKeys[] = { TEXT("error"), TEXT("error_code"), TEXT("code") };

	static const TCHAR* const DefinitiveCodes[] = {
		TEXT("invalid_grant"),
		TEXT("invalid_refresh_token"),
		TEXT("refresh_token_not_found"),
		TEXT("refresh_token_already_used")
	};

	for (const TCHAR* const Key : CodeKeys)
	{
		FString Value;
		if (!Root->TryGetStringField(Key, Value) || Value.IsEmpty())
		{
			continue;
		}
		const FString Lowered = Value.ToLower();
		for (const TCHAR* const Code : DefinitiveCodes)
		{
			if (Lowered == Code)
			{
				return true;
			}
		}
	}

	return false;
}

bool FMantlePlaceAuthLogic::IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc)
{
	return (NowUtc + FTimespan::FromSeconds(ExpirySkewSeconds)) >= ExpiresAtUtc;
}

FString FMantlePlaceAuthLogic::ChooseRefreshToken(const FString& NewRefreshToken, const FString& PriorRefreshToken)
{
	return NewRefreshToken.IsEmpty() ? PriorRefreshToken : NewRefreshToken;
}

EMantlePlaceAuthState FMantlePlaceAuthLogic::NextState(EMantlePlaceAuthState Current, EMantlePlaceAuthEvent Event)
{
	switch (Event)
	{
	case EMantlePlaceAuthEvent::SignOut:
		return EMantlePlaceAuthState::Unauthenticated;

	case EMantlePlaceAuthEvent::Cancel:
		return (Current == EMantlePlaceAuthState::Authenticating)
			? EMantlePlaceAuthState::Unauthenticated
			: Current;

	case EMantlePlaceAuthEvent::BeginSignIn:
		if (Current == EMantlePlaceAuthState::Authenticating || Current == EMantlePlaceAuthState::Refreshing)
		{
			return Current;
		}
		return EMantlePlaceAuthState::Authenticating;

	case EMantlePlaceAuthEvent::SignInSucceeded:
		return (Current == EMantlePlaceAuthState::Authenticating)
			? EMantlePlaceAuthState::Authenticated
			: Current;

	case EMantlePlaceAuthEvent::SignInFailed:
		return (Current == EMantlePlaceAuthState::Authenticating)
			? EMantlePlaceAuthState::Failed
			: Current;

	case EMantlePlaceAuthEvent::BeginRefresh:
		return (Current == EMantlePlaceAuthState::Authenticated)
			? EMantlePlaceAuthState::Refreshing
			: Current;

	case EMantlePlaceAuthEvent::BeginRestore:
		return (Current == EMantlePlaceAuthState::Unauthenticated || Current == EMantlePlaceAuthState::Failed)
			? EMantlePlaceAuthState::Refreshing
			: Current;

	case EMantlePlaceAuthEvent::RefreshSucceeded:
		return (Current == EMantlePlaceAuthState::Refreshing)
			? EMantlePlaceAuthState::Authenticated
			: Current;

	case EMantlePlaceAuthEvent::RefreshFailed:
		return (Current == EMantlePlaceAuthState::Refreshing)
			? EMantlePlaceAuthState::Failed
			: Current;

	default:
		return Current;
	}
}

FString FMantlePlaceAuthLogic::Base64UrlEncode(const TArray<uint8>& Bytes)
{
	FString Encoded = FBase64::Encode(Bytes);
	Encoded.ReplaceInline(TEXT("+"), TEXT("-"));
	Encoded.ReplaceInline(TEXT("/"), TEXT("_"));
	Encoded.ReplaceInline(TEXT("="), TEXT(""));
	return Encoded;
}

FString FMantlePlaceAuthLogic::MakeCodeVerifier(const TArray<uint8>& RandomBytes)
{
	return Base64UrlEncode(RandomBytes);
}

FString FMantlePlaceAuthLogic::MakeCodeChallengeS256(const FString& CodeVerifier)
{
	const FTCHARToUTF8 Utf8(*CodeVerifier);
	FMantlePlaceSha256 Hasher;
	Hasher.Update(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
	uint8 Hash[32];
	Hasher.FinalBytes(Hash);

	TArray<uint8> HashBytes;
	HashBytes.Append(Hash, UE_ARRAY_COUNT(Hash));
	return Base64UrlEncode(HashBytes);
}

FString FMantlePlaceAuthLogic::BuildLoopbackRedirectUri(int32 Port, const FString& CallbackPath)
{
	FString Path = CallbackPath;
	if (!Path.StartsWith(TEXT("/")))
	{
		Path = TEXT("/") + Path;
	}
	return FString::Printf(TEXT("http://127.0.0.1:%d%s"), Port, *Path);
}

FMantlePlaceAuthLogic::ELoopbackPortMode FMantlePlaceAuthLogic::ResolveLoopbackPortMode(
    const TArray<int32>& ConfiguredPorts)
{
	return ConfiguredPorts.Num() > 0 ? ELoopbackPortMode::DeclaredList : ELoopbackPortMode::Ephemeral;
}

bool FMantlePlaceAuthLogic::AcquireEphemeralLoopbackPort(
    TFunctionRef<bool(int32&)> ProposePort,
    TFunctionRef<bool(int32)> TryAcquire,
    int32 MaxAttempts,
    int32& OutPort)
{
	for (int32 Attempt = 0; Attempt < MaxAttempts; ++Attempt)
	{
		int32 Proposed = 0;
		if (!ProposePort(Proposed))
		{
			continue;
		}

		if (TryAcquire(Proposed))
		{
			OutPort = Proposed;
			return true;
		}
	}
	return false;
}

bool FMantlePlaceAuthLogic::SelectLoopbackPort(const TArray<int32>& Ports,
	TFunctionRef<bool(int32)> TryAcquire, int32& OutPort)
{
	for (const int32 Port : Ports)
	{
		if (TryAcquire(Port))
		{
			OutPort = Port;
			return true;
		}
	}
	return false;
}

FString FMantlePlaceAuthLogic::BuildAuthorizeUrl(const FString& WebLoginBaseUrl, const FString& RedirectUri,
	const FString& CodeChallenge, const FString& State)
{
	const FString Base = WebLoginBaseUrl.TrimStartAndEnd();
	const TCHAR Separator = Base.Contains(TEXT("?")) ? TEXT('&') : TEXT('?');
	return FString::Printf(
		TEXT("%s%cresponse_type=code&code_challenge=%s&code_challenge_method=S256&redirect_uri=%s&state=%s"),
		*Base, Separator, *PercentEncode(CodeChallenge), *PercentEncode(RedirectUri), *PercentEncode(State));
}

FString FMantlePlaceAuthLogic::BuildPkceTokenBody(const FString& AuthCode, const FString& CodeVerifier)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("auth_code"), AuthCode);
	Root->SetStringField(TEXT("code_verifier"), CodeVerifier);
	return SerializeCondensed(Root);
}

bool FMantlePlaceAuthLogic::ParseCallbackQuery(const FString& RawQuery, FMantlePlaceAuthCallback& OutCallback)
{
	OutCallback = FMantlePlaceAuthCallback();

	FString Query = RawQuery.TrimStartAndEnd();

	int32 QuestionIdx = INDEX_NONE;
	if (Query.FindChar(TEXT('?'), QuestionIdx))
	{
		Query = Query.RightChop(QuestionIdx + 1);
	}

	int32 HashIdx = INDEX_NONE;
	if (Query.FindChar(TEXT('#'), HashIdx))
	{
		Query = Query.Left(HashIdx);
	}

	if (Query.IsEmpty())
	{
		return false;
	}

	TArray<FString> Pairs;
	Query.ParseIntoArray(Pairs, TEXT("&"), true);

	bool bFoundAny = false;
	for (const FString& Pair : Pairs)
	{
		FString Key;
		FString Value;
		if (!Pair.Split(TEXT("="), &Key, &Value))
		{
			Key = Pair;
		}

		Key = PercentDecode(Key.TrimStartAndEnd());
		Value = PercentDecode(Value);

		if (Key == TEXT("code")) { OutCallback.Code = Value; bFoundAny = true; }
		else if (Key == TEXT("state")) { OutCallback.State = Value; bFoundAny = true; }
		else if (Key == TEXT("error")) { OutCallback.Error = Value; bFoundAny = true; }
		else if (Key == TEXT("error_description")) { OutCallback.ErrorDescription = Value; bFoundAny = true; }
	}

	return bFoundAny;
}

bool FMantlePlaceAuthLogic::IsStateValid(const FString& Expected, const FString& Received)
{
	return !Expected.IsEmpty() && Expected.Equals(Received, ESearchCase::CaseSensitive);
}

namespace
{
	const TCHAR* const BrowserPageStylesheet = TEXT(
		":root{color-scheme:dark}"
		"html,body{height:100%}"
		"body{margin:0;display:flex;align-items:center;justify-content:center;box-sizing:border-box;"
		"padding:2rem;background:#0B0C10;color:#fff;"
		"font-family:system-ui,-apple-system,\"Segoe UI\",Roboto,sans-serif}"
		"main{max-width:26rem;text-align:center}"
		"hr{width:2rem;height:2px;border:0;margin:0 auto 1.5rem;background:#FF7110}"
		".wordmark{margin:0 0 1.75rem;font-size:.75rem;letter-spacing:.18em;text-transform:uppercase;"
		"color:rgba(255,255,255,.65)}"
		"h1{margin:0 0 .625rem;font-size:1.375rem;font-weight:600;letter-spacing:-.01em}"
		".body{margin:0;font-size:.9375rem;line-height:1.55;color:rgba(255,255,255,.7)}"
		".close{margin:1rem 0 0;font-size:.875rem;line-height:1.5;color:rgba(255,255,255,.4)}");

	const TCHAR* const BrowserDoneUrl = TEXT("https://mantle.place/auth/native/done?host=unreal");

	FString BrowserHandOffScript()
	{
		return FString::Printf(
			TEXT("<script>(function(){if(!window.fetch){return}var d=\"%s\";"
				 "fetch(d,{mode:\"no-cors\",cache:\"no-store\"}).then(function(){location.replace(d)},"
				 "function(){});})();</script>"),
			BrowserDoneUrl);
	}

	FString BuildBrowserPage(const TCHAR* Title, const FString& BodyHtml, const FString& Script)
	{
		return FString::Printf(TEXT(
			"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
			"<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
			"<title>%s &middot; Mantle Place</title>"
			"<style>%s</style></head><body><main>"
			"<hr>"
			"<p class=\"wordmark\">Mantle Place</p>"
			"<h1>%s</h1>"
			"<p class=\"body\">%s</p>"
			"<p class=\"close\">You can close this tab and return to the Unreal Editor.</p>"
			"</main>%s</body></html>"),
			Title, BrowserPageStylesheet, Title, *BodyHtml, *Script);
	}
}

FString FMantlePlaceAuthLogic::BuildBrowserSuccessHtml()
{
	return BuildBrowserPage(
		TEXT("Signed in"),
		TEXT("You're signed in to Mantle Place."),
		BrowserHandOffScript());
}

FString FMantlePlaceAuthLogic::BuildBrowserErrorHtml(const FString& Message)
{
	FString Safe = Message;
	Safe.ReplaceInline(TEXT("&"), TEXT("&amp;"));
	Safe.ReplaceInline(TEXT("<"), TEXT("&lt;"));
	Safe.ReplaceInline(TEXT(">"), TEXT("&gt;"));

	return BuildBrowserPage(TEXT("Sign-in failed"), TEXT("Sign-in did not complete: ") + Safe, FString());
}
