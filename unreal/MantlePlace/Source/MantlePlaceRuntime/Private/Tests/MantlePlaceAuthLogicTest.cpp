// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceAuthLogic.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

// PKCE, the callback query grammar, the auth state machine and the token-response shape are read
// from tools/manifest-conformance/corpus/auth/ (HPS-40). These are the vectors where a second host
// most easily goes subtly wrong — an unescaped '+' in a base64url verifier, a case-insensitive
// state comparison, an `expires_in: 0` taken literally — and every one of those failures is silent.

namespace
{
using namespace MantlePlaceConformanceCorpus;

/** Parse a JSON body and read a string field; returns "" if absent. */
FString ReadStringField(const FString& JsonStr, const TCHAR* Field)
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

/** "14fb9c03d97e" -> the six bytes it names. */
TArray<uint8> BytesFromHex(const FString& Hex)
{
	TArray<uint8> Bytes;
	for (int32 Index = 0; Index + 1 < Hex.Len(); Index += 2)
	{
		Bytes.Add(static_cast<uint8>(FParse::HexNumber(*Hex.Mid(Index, 2))));
	}
	return Bytes;
}

FString StateName(EMantlePlaceAuthState State)
{
	switch (State)
	{
		case EMantlePlaceAuthState::Authenticating: return TEXT("Authenticating");
		case EMantlePlaceAuthState::Authenticated:  return TEXT("Authenticated");
		case EMantlePlaceAuthState::Refreshing:     return TEXT("Refreshing");
		case EMantlePlaceAuthState::Failed:         return TEXT("Failed");
		default:                                    return TEXT("Unauthenticated");
	}
}

bool StateFromName(const FString& Name, EMantlePlaceAuthState& OutState)
{
	static const TMap<FString, EMantlePlaceAuthState> Map = {
		{ TEXT("Unauthenticated"), EMantlePlaceAuthState::Unauthenticated },
		{ TEXT("Authenticating"),  EMantlePlaceAuthState::Authenticating },
		{ TEXT("Authenticated"),   EMantlePlaceAuthState::Authenticated },
		{ TEXT("Refreshing"),      EMantlePlaceAuthState::Refreshing },
		{ TEXT("Failed"),          EMantlePlaceAuthState::Failed },
	};
	if (const EMantlePlaceAuthState* Found = Map.Find(Name))
	{
		OutState = *Found;
		return true;
	}
	return false;
}

bool EventFromName(const FString& Name, EMantlePlaceAuthEvent& OutEvent)
{
	static const TMap<FString, EMantlePlaceAuthEvent> Map = {
		{ TEXT("BeginSignIn"),      EMantlePlaceAuthEvent::BeginSignIn },
		{ TEXT("SignInSucceeded"),  EMantlePlaceAuthEvent::SignInSucceeded },
		{ TEXT("SignInFailed"),     EMantlePlaceAuthEvent::SignInFailed },
		{ TEXT("BeginRefresh"),     EMantlePlaceAuthEvent::BeginRefresh },
		{ TEXT("RefreshSucceeded"), EMantlePlaceAuthEvent::RefreshSucceeded },
		{ TEXT("RefreshFailed"),    EMantlePlaceAuthEvent::RefreshFailed },
		{ TEXT("SignOut"),          EMantlePlaceAuthEvent::SignOut },
		{ TEXT("Cancel"),           EMantlePlaceAuthEvent::Cancel },
		{ TEXT("BeginRestore"),     EMantlePlaceAuthEvent::BeginRestore },
	};
	if (const EMantlePlaceAuthEvent* Found = Map.Find(Name))
	{
		OutEvent = *Found;
		return true;
	}
	return false;
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceAuthLogicTest,
	"MantlePlace.Auth.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceAuthLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceAuthLogic;

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("auth"), Cases, LoadError))
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
			AddError(FString::Printf(TEXT("corpus case '%s' has gone missing from the auth group"), Id));
			return nullptr;
		}
		Driven.Add(Found->Id);
		return Found;
	};

	// --- PKCE: base64url, verifier shape, the RFC 7636 challenge, the loopback redirect --------
	if (const FCase* Case = Take(TEXT("auth.pkceVectors")))
	{
		const TSharedPtr<FJsonObject> Root = Case->PayloadObject;
		if (!Root.IsValid())
		{
			AddError(Case->What(TEXT("vector file is not a JSON object")));
		}
		else
		{
			// Each sub-table below is read through an `if (TryGetObjectField(...))`, which does
			// nothing at all if the key is renamed. Assert the keys exist first, or a rename turns
			// the whole PKCE section into a silent no-op.
			for (const TCHAR* Required : { TEXT("base64url"), TEXT("verifier"), TEXT("challengeS256"),
			                               TEXT("redirectUri"), TEXT("authorizeQueryOrder"),
			                               TEXT("percentEncoding") })
			{
				TestTrue(FString::Printf(TEXT("[%s] declares %s"), *Case->Id, Required),
					Root->HasField(Required));
			}

			// base64url (RFC 4648 §5): '+' -> '-', '/' -> '_', no '=' padding.
			const TSharedPtr<FJsonObject>* Base64Url = nullptr;
			if (Root->TryGetObjectField(TEXT("base64url"), Base64Url) && Base64Url != nullptr)
			{
				const TArray<TSharedPtr<FJsonValue>>* Vectors = nullptr;
				if ((*Base64Url)->TryGetArrayField(TEXT("vectors"), Vectors) && Vectors != nullptr)
				{
					for (const TSharedPtr<FJsonValue>& Value : *Vectors)
					{
						const TSharedPtr<FJsonObject> Row = Value->AsObject();
						const FString Hex = RowString(Row, TEXT("bytesHex"));
						TestEqual(FString::Printf(TEXT("[%s] base64url(%s)"), *Case->Id, *Hex),
							FLogic::Base64UrlEncode(BytesFromHex(Hex)),
							RowString(Row, TEXT("encoded")));
					}
				}
			}

			// A verifier built from the stated entropy must have the stated length and charset.
			const TSharedPtr<FJsonObject>* Verifier = nullptr;
			if (Root->TryGetObjectField(TEXT("verifier"), Verifier) && Verifier != nullptr)
			{
				const int32 EntropyBytes = static_cast<int32>(RowNumber(*Verifier, TEXT("entropyBytes"), 32));
				TArray<uint8> Bytes;
				for (int32 Index = 0; Index < EntropyBytes; ++Index)
				{
					Bytes.Add(static_cast<uint8>(Index * 7 + 1));
				}
				const FString Made = FLogic::MakeCodeVerifier(Bytes);
				TestEqual(Case->What(TEXT("verifier encodedLength")),
					Made.Len(), static_cast<int32>(RowNumber(*Verifier, TEXT("encodedLength"))));
				for (const FString& Forbidden : RowStrings(*Verifier, TEXT("mustNotContain")))
				{
					TestFalse(FString::Printf(TEXT("[%s] verifier has no '%s'"), *Case->Id, *Forbidden),
						Made.Contains(Forbidden));
				}
			}

			// The RFC 7636 Appendix B known answer — the one vector that pins S256 end to end.
			const TSharedPtr<FJsonObject>* Challenge = nullptr;
			if (Root->TryGetObjectField(TEXT("challengeS256"), Challenge) && Challenge != nullptr)
			{
				const TSharedPtr<FJsonObject>* Reference = nullptr;
				if ((*Challenge)->TryGetObjectField(TEXT("rfc7636AppendixB"), Reference) && Reference != nullptr)
				{
					TestEqual(Case->What(TEXT("RFC 7636 Appendix B S256 challenge")),
						FLogic::MakeCodeChallengeS256(RowString(*Reference, TEXT("verifier"))),
						RowString(*Reference, TEXT("challenge")));
				}
			}

			// The literal loopback IP, never "localhost" (RFC 8252 §8.3): a hosts-file entry or an
			// IPv6-first resolver can point "localhost" somewhere the callback server is not.
			const TSharedPtr<FJsonObject>* Redirect = nullptr;
			FString RedirectExample;
			if (Root->TryGetObjectField(TEXT("redirectUri"), Redirect) && Redirect != nullptr)
			{
				RedirectExample = RowString(*Redirect, TEXT("example"));

				// The template is the normative shape; the example must be the template
				// instantiated, and the host's constructor must reproduce both — asserting the
				// example alone would let the template drift unread.
				FString Template = RowString(*Redirect, TEXT("template"));
				TestFalse(Case->What(TEXT("states a redirectUri.template")), Template.IsEmpty());
				Template.ReplaceInline(TEXT("{port}"), TEXT("51000"));
				Template.ReplaceInline(TEXT("{path}"), TEXT("/callback"));
				TestEqual(Case->What(TEXT("example instantiates the template")), RedirectExample, Template);

				TestEqual(Case->What(TEXT("loopback redirect uri")),
					FLogic::BuildLoopbackRedirectUri(51000, TEXT("/callback")), RedirectExample);
				TestFalse(Case->What(TEXT("redirect uri never says localhost")),
					RedirectExample.Contains(TEXT("localhost")));
			}

			// The authorize URL: every required param, in the stated order, redirect percent-encoded.
			const FString Url = FLogic::BuildAuthorizeUrl(
				TEXT("https://mantle.place/auth/native"), RedirectExample, TEXT("CHALLENGE123"), TEXT("STATE456"));
			const TArray<FString> QueryOrder = RowStrings(Root, TEXT("authorizeQueryOrder"));
			TestTrue(Case->What(TEXT("states an authorizeQueryOrder")), QueryOrder.Num() > 0);
			int32 Previous = -1;
			for (const FString& Param : QueryOrder)
			{
				const int32 At = Url.Find(Param);
				TestTrue(FString::Printf(TEXT("[%s] authorize url carries %s"), *Case->Id, *Param), At >= 0);
				TestTrue(FString::Printf(TEXT("[%s] %s comes after the previous param"), *Case->Id, *Param),
					At > Previous);
				Previous = At;
			}
			// The order list names bare keys, so it cannot catch a param emitted with an empty value.
			TestTrue(Case->What(TEXT("code_challenge carries its value")),
				Url.Contains(TEXT("code_challenge=CHALLENGE123")));
			TestTrue(Case->What(TEXT("state carries its value")), Url.Contains(TEXT("state=STATE456")));
			TestTrue(Case->What(TEXT("the query starts at the base url")),
				Url.StartsWith(TEXT("https://mantle.place/auth/native?")));

			const TSharedPtr<FJsonObject>* PercentEncoding = nullptr;
			if (Root->TryGetObjectField(TEXT("percentEncoding"), PercentEncoding) && PercentEncoding != nullptr)
			{
				TestTrue(Case->What(TEXT("authorize url percent-encodes redirect_uri")),
					Url.Contains(TEXT("redirect_uri=") + RowString(*PercentEncoding, TEXT("encoded"))));
			}
		}
	}

	// --- The redirect callback query grammar, and the CSRF state comparison --------------------
	if (const FCase* Case = Take(TEXT("auth.callbackQueryVectors")))
	{
		// The recognised-key set is normative: every key the corpus lists must be recognised on
		// its own (a query carrying only that key parses), and its value must land in the matching
		// callback field rather than merely flipping the parsed bit. A key the corpus lists that
		// this host maps nowhere lands nothing and fails here.
		TArray<FString> RecognisedKeys;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("recognisedKeys"), RecognisedKeys);
		}
		TestTrue(Case->What(TEXT("states recognisedKeys")), RecognisedKeys.Num() > 0);
		for (const FString& Key : RecognisedKeys)
		{
			FMantlePlaceAuthCallback Callback;
			TestTrue(FString::Printf(TEXT("[%s] \"%s\" alone is recognised"), *Case->Id, *Key),
				FLogic::ParseCallbackQuery(Key + TEXT("=probe-value"), Callback));
			const FString Landed =
				Key == TEXT("code") ? Callback.Code :
				Key == TEXT("state") ? Callback.State :
				Key == TEXT("error") ? Callback.Error :
				Key == TEXT("error_description") ? Callback.ErrorDescription :
				FString();
			TestEqual(FString::Printf(TEXT("[%s] \"%s\" lands in its callback field"), *Case->Id, *Key),
				Landed, FString(TEXT("probe-value")));
		}

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has callback vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Input = RowString(Row, TEXT("input"));
			const FString Where = FString::Printf(TEXT("[%s] \"%s\""), *Case->Id, *Input);

			FMantlePlaceAuthCallback Callback;
			const bool bParsed = FLogic::ParseCallbackQuery(Input, Callback);
			TestTrue(Where + TEXT(" parsed"), bParsed == RowBool(Row, TEXT("parsed")));
			if (!bParsed)
			{
				continue;
			}
			// Absent in the row means "must be empty" — a parser that leaks a code onto an error
			// callback would otherwise slip through.
			TestEqual(Where + TEXT(" code"), Callback.Code, RowString(Row, TEXT("code")));
			TestEqual(Where + TEXT(" state"), Callback.State, RowString(Row, TEXT("state")));
			TestEqual(Where + TEXT(" error"), Callback.Error, RowString(Row, TEXT("error")));
			TestEqual(Where + TEXT(" errorDescription"),
				Callback.ErrorDescription, RowString(Row, TEXT("errorDescription")));
		}

		const TArray<TSharedPtr<FJsonObject>> StateRows = Rows(*Case, TEXT("stateValidation"));
		TestTrue(Case->What(TEXT("has stateValidation rows")), StateRows.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : StateRows)
		{
			const FString Expected = RowString(Row, TEXT("expected"));
			const FString Received = RowString(Row, TEXT("received"));
			TestTrue(
				FString::Printf(TEXT("[%s] IsStateValid(\"%s\", \"%s\")"), *Case->Id, *Expected, *Received),
				FLogic::IsStateValid(Expected, Received) == RowBool(Row, TEXT("valid")));
		}
	}

	// --- The state machine, driven exhaustively over every (state, event) pair -----------------
	// The corpus lists ordered rules with '*' wildcards and an "unchanged" default; resolving them
	// here — first match wins — asserts all 5x9 cells, not just the ones somebody thought to write.
	if (const FCase* Case = Take(TEXT("auth.stateMachine")))
	{
		const TArray<TSharedPtr<FJsonObject>> Transitions = Rows(*Case, TEXT("transitions"));
		TestTrue(Case->What(TEXT("has transitions")), Transitions.Num() > 0);

		TArray<FString> StateNames;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("states"), StateNames);
		}

		// Every event named anywhere in the table, deduplicated in first-seen order.
		TArray<FString> EventNames;
		for (const TSharedPtr<FJsonObject>& Rule : Transitions)
		{
			EventNames.AddUnique(RowString(Rule, TEXT("event")));
		}

		for (const FString& FromName : StateNames)
		{
			EMantlePlaceAuthState From = EMantlePlaceAuthState::Unauthenticated;
			if (!StateFromName(FromName, From))
			{
				AddError(FString::Printf(TEXT("[%s] corpus names state '%s', which this host does not have"),
					*Case->Id, *FromName));
				continue;
			}

			for (const FString& EventName : EventNames)
			{
				EMantlePlaceAuthEvent Event = EMantlePlaceAuthEvent::BeginSignIn;
				if (!EventFromName(EventName, Event))
				{
					AddError(FString::Printf(TEXT("[%s] corpus names event '%s', which this host does not have"),
						*Case->Id, *EventName));
					continue;
				}

				FString ExpectedName = FromName; // the stated default: unchanged
				for (const TSharedPtr<FJsonObject>& Rule : Transitions)
				{
					if (RowString(Rule, TEXT("event")) != EventName)
					{
						continue;
					}
					const FString RuleFrom = RowString(Rule, TEXT("from"));
					if (RuleFrom != TEXT("*") && RuleFrom != FromName)
					{
						continue;
					}
					const FString To = RowString(Rule, TEXT("to"));
					ExpectedName = (To == TEXT("unchanged")) ? FromName : To;
					break; // first match wins
				}

				TestEqual(
					FString::Printf(TEXT("[%s] %s + %s"), *Case->Id, *FromName, *EventName),
					StateName(FLogic::NextState(From, Event)),
					ExpectedName);
			}
		}

		FString Initial;
		if (Case->PayloadObject.IsValid() && Case->PayloadObject->TryGetStringField(TEXT("initial"), Initial))
		{
			TestEqual(Case->What(TEXT("initial state")), StateName(EMantlePlaceAuthState()), Initial);
		}
	}

	// --- Token responses: the lifetime substitution, the error message, refresh-token retention -
	if (const FCase* Case = Take(TEXT("auth.tokenResponseVectors")))
	{
		for (const TCHAR* Required : { TEXT("vectors"), TEXT("errorPrecedence"),
		                               TEXT("chooseRefreshToken"), TEXT("isExpired") })
		{
			TestTrue(FString::Printf(TEXT("[%s] declares %s"), *Case->Id, Required),
				Case->PayloadObject.IsValid() && Case->PayloadObject->HasField(Required));
		}

		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has token vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Body = RowBodyAsText(Row);
			const FString Where = FString::Printf(TEXT("[%s] %s"), *Case->Id, *Body.Left(60));
			const bool bShouldParse = RowBool(Row, TEXT("parsed"));

			FMantlePlaceAuthTokens Tokens;
			FString Error;
			const bool bParsed = FLogic::ParseTokenResponse(Body, Tokens, Error);
			TestTrue(Where + TEXT(" parsed"), bParsed == bShouldParse);

			if (bParsed)
			{
				// The tokens themselves — without these, a parser that returns true with an empty
				// access token satisfies every other assertion in this file.
				FString Text;
				if (Row->TryGetStringField(TEXT("accessToken"), Text))
				{
					TestEqual(Where + TEXT(" accessToken"), Tokens.AccessToken, Text);
					TestTrue(Where + TEXT(" tokens are usable"), Tokens.IsValid());
				}
				if (Row->TryGetStringField(TEXT("refreshToken"), Text))
				{
					TestEqual(Where + TEXT(" refreshToken"), Tokens.RefreshToken, Text);
				}
				// A body with no `user` object must leave UserId empty rather than inventing one.
				if (!Row->HasField(TEXT("userId")))
				{
					TestTrue(Where + TEXT(" userId empty when the body carries no user"),
						Tokens.UserId.IsEmpty());
				}

				double ExpiresIn = 0.0;
				if (Row->TryGetNumberField(TEXT("expiresInSeconds"), ExpiresIn))
				{
					TestEqual(Where + TEXT(" expiresInSeconds"),
						Tokens.ExpiresInSeconds, static_cast<int32>(ExpiresIn));
				}
				FString UserId;
				if (Row->TryGetStringField(TEXT("userId"), UserId))
				{
					TestEqual(Where + TEXT(" userId"), Tokens.UserId, UserId);
				}
			}
			else
			{
				const FString Contains = RowString(Row, TEXT("errorContains"));
				if (!Contains.IsEmpty())
				{
					TestTrue(Where + TEXT(" errorContains"), Error.Contains(Contains));
				}
			}
		}

		// Error-key precedence: build a body carrying every key from position i onward and assert
		// the key at i is the one whose value surfaces.
		TArray<FString> Precedence;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("errorPrecedence"), Precedence);
		}
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

			FString Error;
			TestTrue(FString::Printf(TEXT("[%s] precedence[%d] parses"), *Case->Id, Index),
				FLogic::ParseErrorResponse(Text, Error));
			TestEqual(FString::Printf(TEXT("[%s] '%s' wins over everything below it"),
					*Case->Id, *Precedence[Index]),
				Error, FString::Printf(TEXT("value-of-%s"), *Precedence[Index]));
		}

		// A grant that omits refresh_token must not wipe the cached one — that would strand the
		// session with a live access token and no way to renew it.
		const TArray<TSharedPtr<FJsonObject>> RefreshRows = Rows(*Case, TEXT("chooseRefreshToken"));
		TestTrue(Case->What(TEXT("has chooseRefreshToken rows")), RefreshRows.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : RefreshRows)
		{
			TestEqual(
				FString::Printf(TEXT("[%s] ChooseRefreshToken(\"%s\", \"%s\")"), *Case->Id,
					*RowString(Row, TEXT("new")), *RowString(Row, TEXT("prior"))),
				FLogic::ChooseRefreshToken(RowString(Row, TEXT("new")), RowString(Row, TEXT("prior"))),
				RowString(Row, TEXT("chosen")));
		}

		// Expiry: the corpus's defaultSkewSeconds must equal the host's own constant (pins the
		// corpus value to the code rather than passing it in as an argument, HPS-11), and the
		// boundary is INCLUSIVE (equal counts as expired).
		const TSharedPtr<FJsonObject>* IsExpired = nullptr;
		if (Case->PayloadObject.IsValid()
			&& Case->PayloadObject->TryGetObjectField(TEXT("isExpired"), IsExpired)
			&& IsExpired != nullptr)
		{
			const int32 Skew = static_cast<int32>(RowNumber(*IsExpired, TEXT("defaultSkewSeconds"), 60));
			TestEqual(Case->What(TEXT("expiry skew")), Skew, FLogic::ExpirySkewSeconds);

			const FDateTime Now(2026, 6, 19, 12, 0, 0);
			TestFalse(Case->What(TEXT("outside the skew window is valid")),
				FLogic::IsExpired(Now, Now + FTimespan::FromSeconds(Skew * 2)));
			TestTrue(Case->What(TEXT("inside the skew window is expired")),
				FLogic::IsExpired(Now, Now + FTimespan::FromSeconds(Skew / 2)));
			TestTrue(Case->What(TEXT("expired exactly at expiresAt (inclusive boundary)")),
				FLogic::IsExpired(Now, Now));
		}
	}

	// --- HPS-41 coverage guard ----------------------------------------------------------------
	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the auth group but nothing in this suite drives it (HPS-41)"),
			*Missing));
	}

	// ==========================================================================================
	// Host-local: Supabase endpoint construction and base-URL validation
	// ==========================================================================================
	// Which GoTrue path this host calls, and how it defends against a half-typed base URL, are
	// deployment plumbing rather than cross-host protocol (DOC-06).
	{
		const FString Expected = TEXT("https://abc.supabase.co/auth/v1/token?grant_type=password");
		TestEqual(TEXT("Password URL from clean base"),
			FLogic::BuildPasswordGrantUrl(TEXT("https://abc.supabase.co")), Expected);
		TestEqual(TEXT("Password URL trims whitespace + multiple slashes"),
			FLogic::BuildPasswordGrantUrl(TEXT("  https://abc.supabase.co//  ")), Expected);
		TestEqual(TEXT("Refresh URL"),
			FLogic::BuildRefreshGrantUrl(TEXT("https://abc.supabase.co")),
			FString(TEXT("https://abc.supabase.co/auth/v1/token?grant_type=refresh_token")));
		TestEqual(TEXT("pkce token url"),
			FLogic::BuildPkceTokenUrl(TEXT("https://abc.supabase.co")),
			FString(TEXT("https://abc.supabase.co/auth/v1/token?grant_type=pkce")));
	}

	// A scheme-only value (a half-typed "https:" left in a BP default or ini) used to slip through
	// an IsEmpty() check and build the hostless URL "https:/auth/v1/token", which DNS-fails and was
	// reported as the misleading "Network error: no response from the platform."
	{
		TestTrue(TEXT("Full https base is valid"), FLogic::IsValidBaseUrl(TEXT("https://abc.supabase.co")));
		TestTrue(TEXT("Trailing slash is valid"), FLogic::IsValidBaseUrl(TEXT("https://abc.supabase.co/")));
		TestTrue(TEXT("Surrounding whitespace tolerated"), FLogic::IsValidBaseUrl(TEXT("  https://abc.supabase.co  ")));
		TestTrue(TEXT("http localhost (local dev) is valid"), FLogic::IsValidBaseUrl(TEXT("http://localhost:3000")));

		TestFalse(TEXT("Empty is invalid"), FLogic::IsValidBaseUrl(TEXT("")));
		TestFalse(TEXT("Whitespace-only is invalid"), FLogic::IsValidBaseUrl(TEXT("   ")));
		TestFalse(TEXT("Scheme-only 'https:' is invalid"), FLogic::IsValidBaseUrl(TEXT("https:")));
		TestFalse(TEXT("Scheme + '//' but no host is invalid"), FLogic::IsValidBaseUrl(TEXT("https://")));
		TestFalse(TEXT("Scheme + empty host (extra slash) is invalid"), FLogic::IsValidBaseUrl(TEXT("https:///path")));
		TestFalse(TEXT("Missing scheme is invalid"), FLogic::IsValidBaseUrl(TEXT("abc.supabase.co")));
		TestFalse(TEXT("Scheme-relative '//host' is invalid"), FLogic::IsValidBaseUrl(TEXT("//mantle.place")));
	}

	// Request bodies: assert fields, not byte order.
	{
		const FString Body = FLogic::BuildPasswordGrantBody(TEXT("user@example.com"), TEXT("hunter2"));
		TestEqual(TEXT("Password body email"), ReadStringField(Body, TEXT("email")), FString(TEXT("user@example.com")));
		TestEqual(TEXT("Password body password"), ReadStringField(Body, TEXT("password")), FString(TEXT("hunter2")));

		TestEqual(TEXT("Refresh body token"),
			ReadStringField(FLogic::BuildRefreshGrantBody(TEXT("refresh-abc-123")), TEXT("refresh_token")),
			FString(TEXT("refresh-abc-123")));

		const FString PkceBody = FLogic::BuildPkceTokenBody(TEXT("auth-code-xyz"), TEXT("verifier-abc"));
		TestEqual(TEXT("pkce body auth_code"), ReadStringField(PkceBody, TEXT("auth_code")), FString(TEXT("auth-code-xyz")));
		TestEqual(TEXT("pkce body code_verifier"), ReadStringField(PkceBody, TEXT("code_verifier")), FString(TEXT("verifier-abc")));
	}

	// The authorize URL joins onto a base that already carries a query.
	{
		const FString UrlWithQuery = FLogic::BuildAuthorizeUrl(
			TEXT("https://mantle.place/auth/native?foo=bar"),
			TEXT("http://127.0.0.1:51000/callback"), TEXT("C"), TEXT("S"));
		TestTrue(TEXT("authorize url appends with '&' when a query exists"),
			UrlWithQuery.Contains(TEXT("?foo=bar&response_type=code")));
	}

	// A callback path without a leading slash still produces a well-formed URI.
	{
		TestEqual(TEXT("loopback redirect uri adds leading slash"),
			FLogic::BuildLoopbackRedirectUri(51000, TEXT("callback")),
			FString(TEXT("http://127.0.0.1:51000/callback")));
	}

	// ----- Loopback port selection -----
	//
	// This fallback was dead code in shipped builds: the shim committed to the first candidate
	// before any real bind was attempted, so a reserved or occupied first port opened a browser tab
	// onto nothing. The loop is only observable once acquisition is injected, which is what these
	// cover — the fall-through, the give-up, and that each candidate is tried once, in order.

	// The default spread must not be consecutive: a Windows Hyper-V/WinNAT reservation is ~100 ports
	// wide, so a consecutive run is swallowed whole (51000-51009 was, and sign-in broke outright).
	{
		const TArray<int32> Defaults = FLogic::DefaultLoopbackPorts();
		TestTrue(TEXT("default loopback spread offers several candidates"), Defaults.Num() >= 3);
		TestEqual(TEXT("default loopback spread still leads with 51000"), Defaults[0], 51000);

		bool bWidelySpaced = true;
		for (int32 Index = 1; Index < Defaults.Num(); ++Index)
		{
			if (Defaults[Index] - Defaults[Index - 1] < 512)
			{
				bWidelySpaced = false;
			}
		}
		TestTrue(TEXT("default loopback candidates are at least 512 apart"), bWidelySpaced);
	}

	// Configured ports win; an empty config falls back to the built-in spread.
	{
		const TArray<int32> Configured = { 41000, 41512 };
		TestTrue(TEXT("configured loopback ports pass through"),
			FLogic::ResolveLoopbackPorts(Configured) == Configured);
		TestTrue(TEXT("no configured ports falls back to the default spread"),
			FLogic::ResolveLoopbackPorts(TArray<int32>()) == FLogic::DefaultLoopbackPorts());
	}

	// The first candidate failing must not abort the search — the regression that broke sign-in.
	{
		TArray<int32> Attempted;
		int32 Selected = 0;
		const bool bAcquired = FLogic::SelectLoopbackPort({ 51000, 51512, 52024 },
			[&Attempted](int32 Port)
			{
				Attempted.Add(Port);
				return Port == 51512;
			}, Selected);

		TestTrue(TEXT("selection succeeds when a later candidate binds"), bAcquired);
		TestEqual(TEXT("selection returns the port that actually bound"), Selected, 51512);
		TestTrue(TEXT("selection stops at the first success"),
			Attempted == TArray<int32>({ 51000, 51512 }));
	}

	// Every candidate failing must be reported, not silently treated as success — otherwise the
	// caller opens a browser onto a port nothing is listening on.
	{
		TArray<int32> Attempted;
		int32 Selected = -7; // sentinel: must survive untouched
		const bool bAcquired = FLogic::SelectLoopbackPort({ 51000, 51512 },
			[&Attempted](int32 Port)
			{
				Attempted.Add(Port);
				return false;
			}, Selected);

		TestFalse(TEXT("selection fails when no candidate binds"), bAcquired);
		TestEqual(TEXT("a failed selection leaves the out port untouched"), Selected, -7);
		TestTrue(TEXT("a failed selection tries every candidate, in order"),
			Attempted == TArray<int32>({ 51000, 51512 }));
	}

	// An empty candidate list is a failure, not a crash or an accidental success.
	{
		int32 Selected = 0;
		const bool bAcquired = FLogic::SelectLoopbackPort({},
			[](int32) { return true; }, Selected);
		TestFalse(TEXT("no candidates means no port"), bAcquired);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
