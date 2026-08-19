// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceAuthLogic.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonWriter.h"
#include "Serialization/JsonSerializer.h"
#include "Misc/Base64.h"

namespace
{
	/** Serialize a JSON object to a condensed (single-line) string suitable for a request body. */
	FString SerializeCondensed(const TSharedRef<FJsonObject>& Root)
	{
		FString Out;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	/** Deserialize a JSON object; returns null on malformed input. */
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

	/** Value of a single hex digit, or -1 if not a hex digit. */
	int32 HexValue(TCHAR Ch)
	{
		if (Ch >= TEXT('0') && Ch <= TEXT('9')) return Ch - TEXT('0');
		if (Ch >= TEXT('a') && Ch <= TEXT('f')) return 10 + (Ch - TEXT('a'));
		if (Ch >= TEXT('A') && Ch <= TEXT('F')) return 10 + (Ch - TEXT('A'));
		return -1;
	}

	/** RFC 3986 percent-encoding over UTF-8 bytes (unreserved set A-Za-z0-9-._~ pass through). */
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

	/** Decode a query-string component ('+' → space, %XX → byte), then interpret the bytes as UTF-8. */
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

	// --- Self-contained SHA-256 (FIPS 180-4). ---
	// Kept local so this pure auth-logic layer needs no platform/engine crypto:
	// FPlatformMisc::GetSHA256Signature asserts "No SHA256 Platform implementation" on Windows in
	// UE 5.8, and this layer must run deterministically headless on any platform. The RFC 7636
	// Appendix B vector in the automation test verifies this implementation end-to-end.
	struct FSha256Context
	{
		uint32 State[8];
		uint64 BitCount;
		uint8 Block[64];
		int32 BlockLen;

		static FORCEINLINE uint32 Ror(uint32 X, uint32 N) { return (X >> N) | (X << (32 - N)); }

		void Init()
		{
			State[0] = 0x6a09e667; State[1] = 0xbb67ae85; State[2] = 0x3c6ef372; State[3] = 0xa54ff53a;
			State[4] = 0x510e527f; State[5] = 0x9b05688c; State[6] = 0x1f83d9ab; State[7] = 0x5be0cd19;
			BitCount = 0;
			BlockLen = 0;
		}

		void Transform(const uint8* Chunk)
		{
			static const uint32 K[64] = {
				0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
				0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
				0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
				0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
				0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
				0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
				0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
				0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
			};

			uint32 W[64];
			for (int32 i = 0; i < 16; ++i)
			{
				W[i] = (uint32(Chunk[i * 4]) << 24) | (uint32(Chunk[i * 4 + 1]) << 16)
					| (uint32(Chunk[i * 4 + 2]) << 8) | uint32(Chunk[i * 4 + 3]);
			}
			for (int32 i = 16; i < 64; ++i)
			{
				const uint32 S0 = Ror(W[i - 15], 7) ^ Ror(W[i - 15], 18) ^ (W[i - 15] >> 3);
				const uint32 S1 = Ror(W[i - 2], 17) ^ Ror(W[i - 2], 19) ^ (W[i - 2] >> 10);
				W[i] = W[i - 16] + S0 + W[i - 7] + S1;
			}

			uint32 a = State[0], b = State[1], c = State[2], d = State[3];
			uint32 e = State[4], f = State[5], g = State[6], h = State[7];
			for (int32 i = 0; i < 64; ++i)
			{
				const uint32 Sig1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
				const uint32 Ch = (e & f) ^ (~e & g);
				const uint32 T1 = h + Sig1 + Ch + K[i] + W[i];
				const uint32 Sig0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
				const uint32 Maj = (a & b) ^ (a & c) ^ (b & c);
				const uint32 T2 = Sig0 + Maj;
				h = g; g = f; f = e; e = d + T1; d = c; c = b; b = a; a = T1 + T2;
			}

			State[0] += a; State[1] += b; State[2] += c; State[3] += d;
			State[4] += e; State[5] += f; State[6] += g; State[7] += h;
		}

		void Update(const uint8* Data, int32 Len)
		{
			for (int32 i = 0; i < Len; ++i)
			{
				Block[BlockLen++] = Data[i];
				if (BlockLen == 64)
				{
					Transform(Block);
					BitCount += 512;
					BlockLen = 0;
				}
			}
		}

		void Final(uint8 OutHash[32])
		{
			const uint64 TotalBits = BitCount + static_cast<uint64>(BlockLen) * 8;

			Block[BlockLen++] = 0x80; // append the '1' bit
			if (BlockLen > 56)
			{
				while (BlockLen < 64) { Block[BlockLen++] = 0; }
				Transform(Block);
				BlockLen = 0;
			}
			while (BlockLen < 56) { Block[BlockLen++] = 0; }

			for (int32 i = 7; i >= 0; --i) // 64-bit big-endian length
			{
				Block[BlockLen++] = static_cast<uint8>((TotalBits >> (i * 8)) & 0xFF);
			}
			Transform(Block);

			for (int32 i = 0; i < 8; ++i)
			{
				OutHash[i * 4]     = static_cast<uint8>((State[i] >> 24) & 0xFF);
				OutHash[i * 4 + 1] = static_cast<uint8>((State[i] >> 16) & 0xFF);
				OutHash[i * 4 + 2] = static_cast<uint8>((State[i] >> 8) & 0xFF);
				OutHash[i * 4 + 3] = static_cast<uint8>(State[i] & 0xFF);
			}
		}
	};

	void Sha256(const uint8* Data, int32 Len, uint8 OutHash[32])
	{
		FSha256Context Ctx;
		Ctx.Init();
		Ctx.Update(Data, Len);
		Ctx.Final(OutHash);
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

	// Require an explicit http(s) scheme, then isolate everything after "://".
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

	// The authority (up to the first '/') must carry a non-empty host. This rejects
	// "https://" (empty) and "https:///path" (empty host before the path).
	int32 SlashIdx = INDEX_NONE;
	const FString Host = Rest.FindChar(TEXT('/'), SlashIdx) ? Rest.Left(SlashIdx) : Rest;
	return !Host.IsEmpty();
}

FString FMantlePlaceAuthLogic::BuildPasswordGrantUrl(const FString& BaseUrl)
{
	return NormalizeBaseUrl(BaseUrl) + TEXT("/auth/v1/token?grant_type=password");
}

FString FMantlePlaceAuthLogic::BuildRefreshGrantUrl(const FString& BaseUrl)
{
	return NormalizeBaseUrl(BaseUrl) + TEXT("/auth/v1/token?grant_type=refresh_token");
}

FString FMantlePlaceAuthLogic::BuildPasswordGrantBody(const FString& Email, const FString& Password)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("email"), Email);
	Root->SetStringField(TEXT("password"), Password);
	return SerializeCondensed(Root);
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
		// Not a token body — surface the error body's message if present.
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

	// A token body that omits (or zeroes) expires_in must not stamp an already-expired session.
	if (Parsed.ExpiresInSeconds <= 0)
	{
		Parsed.ExpiresInSeconds = DefaultAccessTokenLifetimeSeconds;
	}

	// user.id is nested and optional — its absence must not fail the parse.
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

	// GoTrue error shapes vary across versions; try the known keys most-specific-first.
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
		// Sign-out always returns to a clean unauthenticated state.
		return EMantlePlaceAuthState::Unauthenticated;

	case EMantlePlaceAuthEvent::Cancel:
		// Aborting an in-flight browser sign-in returns to a clean state — never latches Failed.
		return (Current == EMantlePlaceAuthState::Authenticating)
			? EMantlePlaceAuthState::Unauthenticated
			: Current;

	case EMantlePlaceAuthEvent::BeginSignIn:
		// Allowed from any settled state (incl. Authenticated, to re-auth as another user);
		// ignored only while a request is already in flight.
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
		// Cold-start restore: refresh a persisted token from a settled, signed-out state.
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
	// The verifier is simply base64url of the entropy; its charset is within RFC 7636's
	// unreserved set, so no further escaping is required.
	return Base64UrlEncode(RandomBytes);
}

FString FMantlePlaceAuthLogic::MakeCodeChallengeS256(const FString& CodeVerifier)
{
	const FTCHARToUTF8 Utf8(*CodeVerifier);
	uint8 Hash[32];
	Sha256(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Hash);

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
	// Use 127.0.0.1 (not "localhost") per RFC 8252 §8.3 — it forces the loopback interface
	// and side-steps DNS / hosts-file surprises.
	return FString::Printf(TEXT("http://127.0.0.1:%d%s"), Port, *Path);
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

FString FMantlePlaceAuthLogic::BuildPkceTokenUrl(const FString& BaseUrl)
{
	return NormalizeBaseUrl(BaseUrl) + TEXT("/auth/v1/token?grant_type=pkce");
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

	// Tolerate a full URL ("http://127.0.0.1:51000/callback?code=..") or a bare query.
	int32 QuestionIdx = INDEX_NONE;
	if (Query.FindChar(TEXT('?'), QuestionIdx))
	{
		Query = Query.RightChop(QuestionIdx + 1);
	}

	// Drop any trailing fragment.
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
	Query.ParseIntoArray(Pairs, TEXT("&"), /*InCullEmpty=*/true);

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

FString FMantlePlaceAuthLogic::BuildBrowserSuccessHtml()
{
	return TEXT(
		"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
		"<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
		"<title>Mantle Place</title></head>"
		"<body style=\"font-family:sans-serif;text-align:center;padding:3rem;color:#222\">"
		"<h2>Signed in to Mantle Place</h2>"
		"<p>Authentication complete. You can close this tab and return to the application.</p>"
		"</body></html>");
}

FString FMantlePlaceAuthLogic::BuildBrowserErrorHtml(const FString& Message)
{
	// Escape the message so an error string can't inject markup into the page.
	FString Safe = Message;
	Safe.ReplaceInline(TEXT("&"), TEXT("&amp;"));
	Safe.ReplaceInline(TEXT("<"), TEXT("&lt;"));
	Safe.ReplaceInline(TEXT(">"), TEXT("&gt;"));

	return FString::Printf(TEXT(
		"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
		"<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
		"<title>Mantle Place</title></head>"
		"<body style=\"font-family:sans-serif;text-align:center;padding:3rem;color:#222\">"
		"<h2>Sign-in failed</h2><p>%s</p>"
		"<p>You can close this tab and return to the application.</p>"
		"</body></html>"), *Safe);
}
