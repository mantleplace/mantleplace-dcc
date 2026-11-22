// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceAuthLogic.h"
#include "Tests/MantlePlaceConformanceCorpus.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
using namespace MantlePlaceConformanceCorpus;

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
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceAuthLogicTest,
	"MantlePlace.Auth.Logic",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceAuthLogicTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

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

	if (const FCase* Case = Take(TEXT("auth.pkceVectors")))
	{
		const TSharedPtr<FJsonObject> Root = Case->PayloadObject;
		if (!Root.IsValid())
		{
			AddError(Case->What(TEXT("vector file is not a JSON object")));
		}
		else
		{
			for (const TCHAR* Required : { TEXT("base64url"), TEXT("verifier"), TEXT("challengeS256"),
			                               TEXT("redirectUri"), TEXT("authorizeQueryOrder"),
			                               TEXT("percentEncoding") })
			{
				TestTrue(FString::Printf(TEXT("[%s] declares %s"), *Case->Id, Required),
					Root->HasField(Required));
			}

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

			const TSharedPtr<FJsonObject>* Redirect = nullptr;
			FString RedirectExample;
			if (Root->TryGetObjectField(TEXT("redirectUri"), Redirect) && Redirect != nullptr)
			{
				RedirectExample = RowString(*Redirect, TEXT("example"));

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

	if (const FCase* Case = Take(TEXT("auth.callbackQueryVectors")))
	{
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

	if (const FCase* Case = Take(TEXT("auth.stateMachine")))
	{
		const TArray<TSharedPtr<FJsonObject>> Transitions = Rows(*Case, TEXT("transitions"));
		TestTrue(Case->What(TEXT("has transitions")), Transitions.Num() > 0);

		TArray<FString> StateNames;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("states"), StateNames);
		}

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

				FString ExpectedName = FromName;
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
					break;
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

	if (const FCase* Case = Take(TEXT("auth.rejectionClassification")))
	{
		TArray<FString> CodeKeys;
		TArray<FString> DefinitiveCodes;
		if (Case->PayloadObject.IsValid())
		{
			Case->PayloadObject->TryGetStringArrayField(TEXT("codeKeys"), CodeKeys);
			Case->PayloadObject->TryGetStringArrayField(TEXT("definitiveCodes"), DefinitiveCodes);
		}
		TestTrue(Case->What(TEXT("declares the code keys")), CodeKeys.Num() > 0);
		TestTrue(Case->What(TEXT("declares the definitive codes")), DefinitiveCodes.Num() > 0);

		const int32 MatrixStatus =
			static_cast<int32>(RowNumber(Case->PayloadObject, TEXT("matrixStatus"), 400.0));
		for (const FString& Key : CodeKeys)
		{
			for (const FString& Code : DefinitiveCodes)
			{
				const FString Body = FString::Printf(TEXT("{\"%s\":\"%s\"}"), *Key, *Code);
				TestTrue(
					Case->What(*FString::Printf(TEXT("%d %s is definitive"), MatrixStatus, *Body)),
					FLogic::IsDefinitiveRejection(MatrixStatus, Body));
			}
		}

		for (const TSharedPtr<FJsonObject>& Row : Rows(*Case, TEXT("vectors")))
		{
			const FString Body = RowBodyAsText(Row);
			const int32 Status = static_cast<int32>(RowNumber(Row, TEXT("status")));
			TestTrue(
				Case->What(*FString::Printf(TEXT("%d %s"), Status, *Body)),
				FLogic::IsDefinitiveRejection(Status, Body) == RowBool(Row, TEXT("definitive")));
		}
	}

	if (const FCase* Case = Take(TEXT("auth.browserPageVectors")))
	{
		const TSharedPtr<FJsonObject> Root = Case->PayloadObject;
		if (!Root.IsValid())
		{
			AddError(Case->What(TEXT("vector file is not a JSON object")));
		}
		else
		{
			const TSharedPtr<FJsonObject>* Copy = nullptr;
			const TSharedPtr<FJsonObject>* Palette = nullptr;
			const TSharedPtr<FJsonObject>* Structure = nullptr;
			const TSharedPtr<FJsonObject>* HandOff = nullptr;
			const TSharedPtr<FJsonObject>* Escaping = nullptr;
			const bool bCopy = Root->TryGetObjectField(TEXT("copy"), Copy);
			const bool bPalette = Root->TryGetObjectField(TEXT("palette"), Palette);
			const bool bStructure = Root->TryGetObjectField(TEXT("structure"), Structure);
			const bool bHandOff = Root->TryGetObjectField(TEXT("handOff"), HandOff);
			const bool bEscaping = Root->TryGetObjectField(TEXT("escaping"), Escaping);

			TestTrue(Case->What(TEXT("states a copy section")), bCopy);
			TestTrue(Case->What(TEXT("states a palette section")), bPalette);
			TestTrue(Case->What(TEXT("states a structure section")), bStructure);
			TestTrue(Case->What(TEXT("states a handOff section")), bHandOff);
			TestTrue(Case->What(TEXT("states an escaping section")), bEscaping);

			const bool bShaped = bCopy && bPalette && bStructure && bHandOff && bEscaping;
			if (bShaped)
			{
				auto Required = [this, Case](const TSharedPtr<FJsonObject>& Row, const TCHAR* Key) -> FString
				{
					const FString Value = RowString(Row, Key);
					TestTrue(Case->What(*FString::Printf(TEXT("states '%s'"), Key)), !Value.IsEmpty());
					return Value;
				};

				auto RequiredList = [this, Case](const TSharedPtr<FJsonObject>& Row, const TCHAR* Key)
				{
					TArray<FString> Values = RowStrings(Row, Key);
					TestTrue(Case->What(*FString::Printf(TEXT("states a non-empty '%s'"), Key)),
						Values.Num() > 0);
					return Values;
				};

				FString ThisHostName;
				const TSharedPtr<FJsonObject>* HostNames = nullptr;
				if ((*Copy)->TryGetObjectField(TEXT("hostNames"), HostNames))
				{
					for (const TPair<FString, TSharedPtr<FJsonValue>>& Entry : (*HostNames)->Values)
					{
						const FString Name = Entry.Value.IsValid() ? Entry.Value->AsString() : FString();
						TestTrue(Case->What(*FString::Printf(TEXT("names host '%s'"), *Entry.Key)),
							!Name.IsEmpty());
						if (Entry.Key == HostKey())
						{
							ThisHostName = Name;
						}
					}
				}

				TestTrue(Case->What(TEXT("and names this one")), !ThisHostName.IsEmpty());
				const FString Closing =
					Required(*Copy, TEXT("closing")).Replace(TEXT("{hostName}"), *ThisHostName);

				const FString Success = FLogic::BuildBrowserSuccessHtml();
				const FString Failure = FLogic::BuildBrowserErrorHtml(TEXT("the server said no"));

				const FString Pages[] = { Success, Failure };
				const TCHAR* const Names[] = { TEXT("success"), TEXT("failure") };
				for (int32 Index = 0; Index < 2; ++Index)
				{
					const TCHAR* const What = Names[Index];
					const FString& Html = Pages[Index];

					for (const FString& Token : RequiredList(*Structure, TEXT("bothPagesContain")))
					{
						TestTrue(Case->What(*FString::Printf(TEXT("%s declares %s"), What, *Token)),
							Html.Contains(Token, ESearchCase::CaseSensitive));
					}

					for (const FString& Forbidden : RequiredList(*Structure, TEXT("bothPagesLack")))
					{
						TestFalse(Case->What(*FString::Printf(TEXT("%s has no %s"), What, *Forbidden)),
							Html.Contains(Forbidden, ESearchCase::CaseSensitive));
					}

					TestTrue(Case->What(*FString::Printf(TEXT("%s: the brand ground"), What)),
						Html.Contains(Required(*Palette, TEXT("groundDeclaration")), ESearchCase::CaseSensitive));
					TestTrue(Case->What(*FString::Printf(TEXT("%s: the brand accent"), What)),
						Html.Contains(Required(*Palette, TEXT("accentDeclaration")), ESearchCase::CaseSensitive));
					TestTrue(Case->What(*FString::Printf(TEXT("%s: a system face, never a webfont"), What)),
						Html.Contains(Required(*Palette, TEXT("fontStackHead")), ESearchCase::CaseSensitive));

					TestTrue(Case->What(*FString::Printf(TEXT("%s: the wordmark"), What)),
						Html.Contains(Required(*Copy, TEXT("wordmark")), ESearchCase::CaseSensitive));
					TestTrue(Case->What(*FString::Printf(TEXT("%s: the tab title carries the brand"), What)),
						Html.Contains(Required(*Copy, TEXT("titleSuffix")), ESearchCase::CaseSensitive));
					TestTrue(Case->What(*FString::Printf(TEXT("%s: tells the user to close the tab"), What)),
						Html.Contains(Closing, ESearchCase::CaseSensitive));
				}

				TestTrue(Case->What(TEXT("the success heading")),
					Success.Contains(Required(*Copy, TEXT("successTitle")), ESearchCase::CaseSensitive));
				TestTrue(Case->What(TEXT("and its sentence")),
					Success.Contains(Required(*Copy, TEXT("successBody")), ESearchCase::CaseSensitive));
				TestTrue(Case->What(TEXT("the failure heading")),
					Failure.Contains(Required(*Copy, TEXT("failureTitle")), ESearchCase::CaseSensitive));
				TestTrue(Case->What(TEXT("and its sentence")),
					Failure.Contains(Required(*Copy, TEXT("failureBodyPrefix")), ESearchCase::CaseSensitive));

				const FString DoneUrl =
					Required(*HandOff, TEXT("urlTemplate")).Replace(TEXT("{hostId}"), HostKey());
				TestTrue(Case->What(TEXT("the hand-off target the corpus states")),
					Success.Contains(DoneUrl, ESearchCase::CaseSensitive));

				for (const FString& Token : RequiredList(*HandOff, TEXT("successPageContains")))
				{
					TestTrue(Case->What(*FString::Printf(TEXT("the hand-off uses %s"), *Token)),
						Success.Contains(Token, ESearchCase::CaseSensitive));
				}

				for (const FString& Forbidden : RequiredList(*HandOff, TEXT("successPageLacks")))
				{
					TestFalse(Case->What(*FString::Printf(TEXT("the hand-off is not %s"), *Forbidden)),
						Success.Contains(Forbidden, ESearchCase::CaseSensitive));
				}

				const bool bSuccessOnly = RowBool(*HandOff, TEXT("successOnly"));
				TestTrue(Case->What(TEXT("the corpus says only success hands off")), bSuccessOnly);
				if (bSuccessOnly)
				{
					TestFalse(Case->What(TEXT("so the failure page does not carry the hand-off target")),
						Failure.Contains(DoneUrl, ESearchCase::CaseSensitive));
				}

				for (const FString& Forbidden : RequiredList(*HandOff, TEXT("failurePageLacks")))
				{
					TestFalse(Case->What(*FString::Printf(TEXT("failure carries no %s"), *Forbidden)),
						Failure.Contains(Forbidden, ESearchCase::CaseSensitive));
				}

				const FString BodyPrefix = Required(*Copy, TEXT("failureBodyPrefix"));
				for (const FString& Character : RequiredList(*Escaping, TEXT("order")))
				{
					TestFalse(Case->What(*FString::Printf(TEXT("'%s' never reaches the page raw"), *Character)),
						FLogic::BuildBrowserErrorHtml(Character)
							.Contains(BodyPrefix + Character + TEXT("<"), ESearchCase::CaseSensitive));
				}

				const TArray<TSharedPtr<FJsonValue>>* EscapeVectors = nullptr;
				const bool bHasVectors =
					(*Escaping)->TryGetArrayField(TEXT("vectors"), EscapeVectors) && EscapeVectors != nullptr;
				TestTrue(Case->What(TEXT("states escaping vectors")), bHasVectors && EscapeVectors->Num() > 0);
				if (bHasVectors)
				{
					for (const TSharedPtr<FJsonValue>& Value : *EscapeVectors)
					{
						const TSharedPtr<FJsonObject>* Row = nullptr;
						if (!Value.IsValid() || !Value->TryGetObject(Row) || Row == nullptr)
						{
							AddError(Case->What(TEXT("an escaping vector is not an object")));
							continue;
						}
						const FString Raw = Required(*Row, TEXT("raw"));
						TestTrue(Case->What(*FString::Printf(TEXT("escaping '%s'"), *Raw)),
							FLogic::BuildBrowserErrorHtml(Raw).Contains(
								BodyPrefix + Required(*Row, TEXT("escaped")), ESearchCase::CaseSensitive));
					}
				}
			}
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the auth group but nothing in this suite drives it (HPS-41)"),
			*Missing));
	}

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

	{
		TestEqual(TEXT("Refresh body token"),
			ReadStringField(FLogic::BuildRefreshGrantBody(TEXT("refresh-abc-123")), TEXT("refresh_token")),
			FString(TEXT("refresh-abc-123")));

		const FString PkceBody = FLogic::BuildPkceTokenBody(TEXT("auth-code-xyz"), TEXT("verifier-abc"));
		TestEqual(TEXT("pkce body auth_code"), ReadStringField(PkceBody, TEXT("auth_code")), FString(TEXT("auth-code-xyz")));
		TestEqual(TEXT("pkce body code_verifier"), ReadStringField(PkceBody, TEXT("code_verifier")), FString(TEXT("verifier-abc")));
	}

	{
		const FString UrlWithQuery = FLogic::BuildAuthorizeUrl(
			TEXT("https://mantle.place/auth/native?foo=bar"),
			TEXT("http://127.0.0.1:51000/callback"), TEXT("C"), TEXT("S"));
		TestTrue(TEXT("authorize url appends with '&' when a query exists"),
			UrlWithQuery.Contains(TEXT("?foo=bar&response_type=code")));
	}

	{
		TestEqual(TEXT("loopback redirect uri adds leading slash"),
			FLogic::BuildLoopbackRedirectUri(51000, TEXT("callback")),
			FString(TEXT("http://127.0.0.1:51000/callback")));
	}

	{
		TestTrue(TEXT("no configured ports means the OS picks"),
		         FLogic::ResolveLoopbackPortMode(TArray<int32>()) == FLogic::ELoopbackPortMode::Ephemeral);
		TestTrue(TEXT("any configured port switches to the declared list"),
		         FLogic::ResolveLoopbackPortMode({ 41000 }) == FLogic::ELoopbackPortMode::DeclaredList);
	}

	{
		TArray<int32> Proposed;
		TArray<int32> Attempted;
		int32 NextPort = 60000;
		int32 Selected = 0;

		const bool bAcquired = FLogic::AcquireEphemeralLoopbackPort(
		    [&Proposed, &NextPort](int32& OutPort)
		    {
			    OutPort = NextPort++;
			    Proposed.Add(OutPort);
			    return true;
		    },
		    [&Attempted](int32 Port)
		    {
			    Attempted.Add(Port);
			    return Port == 60002;
		    },
		    5, Selected);

		TestTrue(TEXT("an ephemeral acquire recovers from a lost race"), bAcquired);
		TestEqual(TEXT("it returns the port that actually bound"), Selected, 60002);
		TestTrue(TEXT("and it got there by proposing fresh ports, not by retrying one"),
		         Proposed == TArray<int32>({ 60000, 60001, 60002 }));
		TestTrue(TEXT("every proposal was actually attempted"), Attempted == Proposed);
	}

	{
		int32 Proposals = 0;
		int32 Selected = -7;
		const bool bAcquired = FLogic::AcquireEphemeralLoopbackPort(
		    [&Proposals](int32&)
		    { ++Proposals; return false; },
		    [](int32)
		    { return true; },
		    4, Selected);

		TestFalse(TEXT("a probe that never yields a port fails"), bAcquired);
		TestEqual(TEXT("and leaves the out port untouched"), Selected, -7);
		TestEqual(TEXT("having tried its full budget"), Proposals, 4);
	}

	{
		int32 Proposals = 0;
		int32 Selected = -7;
		const bool bAcquired = FLogic::AcquireEphemeralLoopbackPort(
		    [&Proposals](int32& OutPort)
		    { OutPort = 60000 + ++Proposals; return true; },
		    [](int32)
		    { return false; },
		    3, Selected);

		TestFalse(TEXT("an ephemeral acquire that never binds fails"), bAcquired);
		TestEqual(TEXT("after exactly its allowed attempts"), Proposals, 3);
		TestEqual(TEXT("leaving the out port untouched"), Selected, -7);
	}

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

	{
		TArray<int32> Attempted;
		int32 Selected = -7;
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

	{
		int32 Selected = 0;
		const bool bAcquired = FLogic::SelectLoopbackPort({},
			[](int32) { return true; }, Selected);
		TestFalse(TEXT("no candidates means no port"), bAcquired);
	}

	return true;
}

#endif
