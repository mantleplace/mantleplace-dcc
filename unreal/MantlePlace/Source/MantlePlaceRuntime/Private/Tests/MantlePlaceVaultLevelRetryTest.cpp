// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultClient.h"
#include "UObject/Package.h"

// The vault client's answer to a refused 3.0 level request, driven at the highest seam a headless suite
// reaches. The HTTP request itself cannot be sent here: the client has no transport seam, and with no
// auth system or session it refuses a request before building one. So the test hands the client's answer handler
// the platform's response (status and body) as if the first request had gone out, and reads what the
// client does next: the request it asks for again (OnLevelRequestedNative fires before every level
// request, signed in or not), the names it carries to the download, and the failure it reports.

/** Reaches the client's private answer path; declared a friend by UMantlePlaceVaultClient for the suite only. */
struct FMantlePlaceVaultClientTestAccess
{
	static void AsIfLevelRequestSent(UMantlePlaceVaultClient& Client, const FString& OrderId, const FString& Level,
		const TArray<FString>& Files, bool bFresh = true)
	{
		Client.PendingDownloadOrderId = OrderId;
		Client.PendingDownloadLevel = Level;
		Client.PendingDownloadFiles = Files;
		if (bFresh)
		{
			Client.LevelAbsentFiles.Reset();
		}
	}

	static void Answer(UMantlePlaceVaultClient& Client, int32 ResponseCode, const FString& Content)
	{
		Client.HandleDownloadAnswer(ResponseCode, Content);
	}

	static const TArray<FString>& AbsentSoFar(const UMantlePlaceVaultClient& Client) { return Client.LevelAbsentFiles; }
};

namespace
{
struct FRequested
{
	FString OrderId;
	FString Level;
	TArray<FString> Files;
};

struct FPresigned
{
	int32 Count = 0;
	bool bSuccess = true;
	FString Message;
};

/** A client with its level requests and presign results recorded. */
struct FRetryFixture
{
	UMantlePlaceVaultClient* Client = nullptr;
	TSharedRef<TArray<FRequested>> Requests = MakeShared<TArray<FRequested>>();
	TSharedRef<FPresigned> Presigned = MakeShared<FPresigned>();

	FRetryFixture()
	{
		Client = NewObject<UMantlePlaceVaultClient>(GetTransientPackage());
		Client->AddToRoot();
		TSharedRef<TArray<FRequested>> R = Requests;
		Client->OnLevelRequestedNative.AddLambda([R](const FString& OrderId, const FString& Level, const TArray<FString>& Files)
		{
			R->Add({ OrderId, Level, Files });
		});
		TSharedRef<FPresigned> P = Presigned;
		Client->OnPresignedUrlReadyNative.AddLambda([P](bool bSuccess, const FMantlePlacePresignedDownload&, const FString& Message)
		{
			++P->Count;
			P->bSuccess = bSuccess;
			P->Message = Message;
		});
	}

	~FRetryFixture()
	{
		Client->OnLevelRequestedNative.Clear();
		Client->OnPresignedUrlReadyNative.Clear();
		Client->RemoveFromRoot();
	}
};

const TCHAR* const RetryOrderId = TEXT("order-retry");

FString Refusal(const FString& FilesJson)
{
	return FString::Printf(TEXT("{\"error\":\"This bundle holds no file named x\",\"code\":\"file_not_in_bundle\","
		"\"reason\":\"unknown_file\",\"files\":%s}"), *FilesJson);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultLevelRetryTest,
	"MantlePlace.Vault.Levels.NotInBundleClient",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultLevelRetryTest::RunTest(const FString& Parameters)
{
	const TArray<FString> Asked = { TEXT("Elevation/Elevation.png"), TEXT("Basemap/Roads.csv"), TEXT("Landcover/Trees.csv") };

	// A refusal naming a requested file: the same order and level are asked for again without it.
	{
		FRetryFixture F;
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, TEXT("Min"), Asked);
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Basemap/Roads.csv\"]")));
		if (TestEqual(TEXT("one more level request is made"), F.Requests->Num(), 1))
		{
			const FRequested& Again = (*F.Requests)[0];
			TestEqual(TEXT("for the same order"), Again.OrderId, FString(RetryOrderId));
			TestEqual(TEXT("at the same level"), Again.Level, FString(TEXT("Min")));
			TestEqual(TEXT("naming the rest, in the order asked"), FString::Join(Again.Files, TEXT(",")),
				FString(TEXT("Elevation/Elevation.png,Landcover/Trees.csv")));
		}
		TestEqual(TEXT("the refused name is carried to the download, to be held against its manifest"),
			FString::Join(FMantlePlaceVaultClientTestAccess::AbsentSoFar(*F.Client), TEXT(",")), FString(TEXT("Basemap/Roads.csv")));
		// This client has no auth system, so the second request is refused before it is sent; signed in, it goes
		// out. Either way the one result is the second request's, never the refusal of the first.
		TestEqual(TEXT("one result"), F.Presigned->Count, 1);
		TestFalse(TEXT("the refusal itself is not reported as the result"), F.Presigned->Message.Contains(TEXT("file_not_in_bundle")));
	}

	// A second refusal adds to the names carried; the list asked for keeps shrinking, so the retries end.
	{
		FRetryFixture F;
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, TEXT("Med"), Asked);
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Basemap/Roads.csv\"]")));
		// As if the second request went out signed in and was refused for another file.
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, TEXT("Med"),
			{ TEXT("Elevation/Elevation.png"), TEXT("Landcover/Trees.csv") }, /*bFresh*/ false);
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Landcover/Trees.csv\"]")));
		if (TestEqual(TEXT("two requests in all"), F.Requests->Num(), 2))
		{
			TestEqual(TEXT("the second names only what is left"), FString::Join((*F.Requests)[1].Files, TEXT(",")),
				FString(TEXT("Elevation/Elevation.png")));
		}
		TestEqual(TEXT("both refused names are carried"),
			FString::Join(FMantlePlaceVaultClientTestAccess::AbsentSoFar(*F.Client), TEXT(",")),
			FString(TEXT("Basemap/Roads.csv,Landcover/Trees.csv")));
	}

	// A refusal naming a file never requested ends the request: no new request, a failure in the platform's words.
	{
		FRetryFixture F;
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, TEXT("Max"), Asked);
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Site/Site.ifc\"]")));
		TestEqual(TEXT("no request is made again"), F.Requests->Num(), 0);
		TestEqual(TEXT("one result"), F.Presigned->Count, 1);
		TestFalse(TEXT("a failure, which ends the import"), F.Presigned->bSuccess);
		TestTrue(TEXT("naming the platform's code"), F.Presigned->Message.Contains(TEXT("file_not_in_bundle")));
	}

	// A refusal that would leave nothing to ask for ends it too.
	{
		FRetryFixture F;
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, TEXT("Max"), { TEXT("Basemap/Roads.csv") });
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Basemap/Roads.csv\"]")));
		TestEqual(TEXT("no request is made again"), F.Requests->Num(), 0);
		TestFalse(TEXT("a failure"), F.Presigned->bSuccess);
	}

	// A 2.x request (no level pending) is never asked again, whatever the refusal says.
	{
		FRetryFixture F;
		FMantlePlaceVaultClientTestAccess::AsIfLevelRequestSent(*F.Client, RetryOrderId, FString(), Asked);
		FMantlePlaceVaultClientTestAccess::Answer(*F.Client, 404, Refusal(TEXT("[\"Basemap/Roads.csv\"]")));
		TestEqual(TEXT("no request is made again"), F.Requests->Num(), 0);
		TestFalse(TEXT("a failure"), F.Presigned->bSuccess);
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
