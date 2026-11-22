// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceAuthLogic.h"
#include "MantlePlaceClientLogic.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IPluginManager.h"

namespace
{
const TCHAR* const UpdateBody =
	TEXT("{\"error\":\"Update the Mantle Place plugin.\",\"code\":\"client_update_required\",")
	TEXT("\"client\":\"unreal\",\"version\":\"0.8.1\",\"minimumVersion\":\"0.9.0\"}");
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceClientHeaderTest,
	"MantlePlace.Client.Header",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceClientHeaderTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceClientLogic;

	TestEqual(TEXT("header name"), FString(FLogic::HeaderName()), FString(TEXT("Mantle-Place-Client")));
	TestEqual(TEXT("plain version"), FLogic::BuildHeaderValue(TEXT("0.8.1")), FString(TEXT("unreal/0.8.1")));
	TestEqual(TEXT("pre-release tail"), FLogic::BuildHeaderValue(TEXT("1.0.0-beta.2")), FString(TEXT("unreal/1.0.0-beta.2")));
	TestEqual(TEXT("surrounding space is dropped"), FLogic::BuildHeaderValue(TEXT(" 0.8.1 ")), FString(TEXT("unreal/0.8.1")));

	TestEqual(TEXT("empty version"), FLogic::BuildHeaderValue(FString()), FString(TEXT("unreal/unknown")));
	TestEqual(TEXT("free text is never sent"), FLogic::BuildHeaderValue(TEXT("my build\r\nX-Evil: 1")), FString(TEXT("unreal/unknown")));
	TestEqual(TEXT("not a version"), FLogic::BuildHeaderValue(TEXT("latest")), FString(TEXT("unreal/unknown")));

	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
	if (!TestTrue(TEXT("the plugin is found by name"), Plugin.IsValid()))
	{
		return false;
	}
	const FString DescriptorVersion = Plugin->GetDescriptor().VersionName;
	TestFalse(TEXT("the descriptor states a version"), DescriptorVersion.IsEmpty());

	const FString ExpectedHeader = FString::Printf(TEXT("unreal/%s"), *DescriptorVersion);
	TestEqual(TEXT("the install's header value"), FMantlePlaceClientHttp::HeaderValue(), ExpectedHeader);

	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Get =
		FMantlePlaceClientHttp::CreateRequest(TEXT("GET"), TEXT("https://mantle.place/api/v1/vault/bundles"));
	TestEqual(TEXT("list request: header"), Get->GetHeader(FLogic::HeaderName()), ExpectedHeader);
	TestEqual(TEXT("list request: verb"), Get->GetVerb(), FString(TEXT("GET")));
	TestEqual(TEXT("list request: url"), Get->GetURL(), FString(TEXT("https://mantle.place/api/v1/vault/bundles")));

	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Post =
		FMantlePlaceClientHttp::CreateRequest(TEXT("POST"), TEXT("https://mantle.place/api/v1/auth/native/refresh"));
	TestEqual(TEXT("auth request: header"), Post->GetHeader(FLogic::HeaderName()), ExpectedHeader);
	TestEqual(TEXT("auth request: verb"), Post->GetVerb(), FString(TEXT("POST")));

	Post->SetHeader(TEXT("Accept"), TEXT("application/json"));
	TestEqual(TEXT("the client header survives the caller's own headers"), Post->GetHeader(FLogic::HeaderName()), ExpectedHeader);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceClientUpdateRequiredTest,
	"MantlePlace.Client.UpdateRequired",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceClientUpdateRequiredTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceClientLogic;

	FMantlePlaceClientUpdate Update;
	if (!TestTrue(TEXT("a 426 client_update_required is an update"), FLogic::ParseClientUpdateRequired(426, UpdateBody, Update)))
	{
		return false;
	}
	TestEqual(TEXT("sent version"), Update.SentVersion, FString(TEXT("0.8.1")));
	TestEqual(TEXT("minimum version"), Update.MinimumVersion, FString(TEXT("0.9.0")));
	TestEqual(TEXT("download link"), Update.DownloadUrl, FString(FLogic::DownloadUrl()));
	TestTrue(TEXT("the sentence names this version"), Update.Message.Contains(TEXT("0.8.1")));
	TestTrue(TEXT("the sentence names the required version"), Update.Message.Contains(TEXT("0.9.0")));
	TestTrue(TEXT("the sentence carries the download link"), Update.Message.Contains(Update.DownloadUrl));

	FMantlePlaceClientUpdate Bare;
	TestTrue(TEXT("code alone is enough"), FLogic::ParseClientUpdateRequired(426, TEXT("{\"code\":\"client_update_required\"}"), Bare));
	TestTrue(TEXT("a bare update still links the download"), Bare.Message.Contains(Bare.DownloadUrl));

	FMantlePlaceClientUpdate Other;
	TestFalse(TEXT("a 400 with the code is not an update"), FLogic::ParseClientUpdateRequired(400, UpdateBody, Other));
	TestFalse(TEXT("a 401 with the code is not an update"), FLogic::ParseClientUpdateRequired(401, UpdateBody, Other));
	TestFalse(TEXT("a 200 is not an update"), FLogic::ParseClientUpdateRequired(200, UpdateBody, Other));
	TestFalse(TEXT("a 426 with another code is not an update"),
		FLogic::ParseClientUpdateRequired(426, TEXT("{\"code\":\"something_else\"}"), Other));
	TestFalse(TEXT("a 426 with no code is not an update"),
		FLogic::ParseClientUpdateRequired(426, TEXT("{\"error\":\"Upgrade Required\"}"), Other));
	TestFalse(TEXT("a bare 426 from a proxy (HTML) is not an update"),
		FLogic::ParseClientUpdateRequired(426, TEXT("<html>Upgrade Required</html>"), Other));
	TestFalse(TEXT("a 426 with an empty body is not an update"), FLogic::ParseClientUpdateRequired(426, FString(), Other));

	TestFalse(TEXT("sign-in: a 426 never discards the stored session"),
		FMantlePlaceAuthLogic::IsDefinitiveRejection(426, UpdateBody));

	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceClientConformanceTest,
	"MantlePlace.Client.Conformance",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceClientConformanceTest::RunTest(const FString& Parameters)
{
	if (MantlePlaceConformanceCorpus::SkipWithoutCorpus(*this))
	{
		return true;
	}

	using namespace MantlePlaceConformanceCorpus;
	using FLogic = FMantlePlaceClientLogic;

	static const TCHAR* const ConsumedExpectationKeys[] = {
		TEXT("status"),
		TEXT("code"),
		TEXT("sentVersion"),
		TEXT("minimumVersion"),
	};

	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("client"), Cases, LoadError))
	{
		AddError(LoadError);
		return false;
	}
	if (!TestTrue(TEXT("the client group holds a case"), Cases.Num() > 0))
	{
		return false;
	}

	TSet<FString> Driven;
	for (const FCase& Case : Cases)
	{
		const TSharedPtr<FJsonObject>* Response = nullptr;
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (!Case.PayloadObject.IsValid() || !Case.PayloadObject->TryGetObjectField(TEXT("response"), Response)
			|| !(*Response)->TryGetObjectField(TEXT("body"), Body))
		{
			AddError(FString::Printf(TEXT("[%s] the exchange has no response body"), *Case.Id));
			continue;
		}
		const int32 Status = static_cast<int32>((*Response)->GetNumberField(TEXT("status")));
		FString BodyText;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&BodyText);
		FJsonSerializer::Serialize(Body->ToSharedRef(), Writer);

		FMantlePlaceClientUpdate Update;
		const bool bIsUpdate = FLogic::ParseClientUpdateRequired(Status, BodyText, Update);
		Driven.Add(Case.Id);

		int32 WantStatus = 0;
		if (WantsInt(Case, TEXT("status"), WantStatus))
		{
			TestEqual(*FString::Printf(TEXT("[%s] status"), *Case.Id), Status, WantStatus);
		}
		FString WantCode;
		if (WantsString(Case, TEXT("code"), WantCode))
		{
			TestEqual(*FString::Printf(TEXT("[%s] read as an update exactly when the code is client_update_required"), *Case.Id),
				bIsUpdate, Status == 426 && WantCode == FLogic::UpdateRequiredCode());
		}
		FString WantSent;
		if (WantsString(Case, TEXT("sentVersion"), WantSent))
		{
			TestEqual(*FString::Printf(TEXT("[%s] the version sent"), *Case.Id), Update.SentVersion, WantSent);
			TestTrue(*FString::Printf(TEXT("[%s] the sentence names it"), *Case.Id), Update.Message.Contains(WantSent));
		}
		FString WantMinimum;
		if (WantsString(Case, TEXT("minimumVersion"), WantMinimum))
		{
			TestEqual(*FString::Printf(TEXT("[%s] the minimum"), *Case.Id), Update.MinimumVersion, WantMinimum);
			TestTrue(*FString::Printf(TEXT("[%s] the sentence names it"), *Case.Id), Update.Message.Contains(WantMinimum));
		}
		if (bIsUpdate)
		{
			TestTrue(*FString::Printf(TEXT("[%s] the sentence carries the download link"), *Case.Id),
				Update.Message.Contains(FLogic::DownloadUrl()));
			TestFalse(*FString::Printf(TEXT("[%s] the stored session survives it"), *Case.Id),
				FMantlePlaceAuthLogic::IsDefinitiveRejection(Status, BodyText));
		}

		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(TEXT("corpus case '%s' loaded but nothing drove it (HPS-41)"), *Missing));
	}
	return true;
}

#endif
