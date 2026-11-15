// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultLogic.h"
#include "MantlePlaceVaultTypes.h"

// An MPB 3.0 order's download: the offer a listing row carries, the request that names one level and
// this host's files, and the answer's link marked as that level's. Built against a test double of the
// published Platform API description (the `mpb3` members of the vault list and download routes); a 2.x
// order keeps its view request, asserted byte for byte in MantlePlace.Vault.Logic.

namespace
{
using FLogic = FMantlePlaceVaultLogic;

/** One listing row: a 2.x row plus, when Offer is not empty, an `mpb3` member holding it. */
FString ListWithRow(const FString& Offer)
{
	const FString Row = FString(TEXT("{\"id\":\"order-3\",\"status\":\"available\",\"sizeBytes\":900,\"sha256\":\"ab\""))
		+ (Offer.IsEmpty() ? FString() : FString(TEXT(",\"mpb3\":")) + Offer)
		+ TEXT("}");
	return FString(TEXT("{\"bundles\":[")) + Row + TEXT("]}");
}

const TCHAR* const LevelsJson =
	TEXT("\"levels\":{")
	TEXT("\"Max\":{\"sizeBytes\":4000000000,\"counts\":{\"rows\":800,\"features\":120,\"triangles\":4000000,\"pixels\":16000000}},")
	TEXT("\"Med\":{\"sizeBytes\":1400000000,\"counts\":{\"rows\":400,\"features\":120,\"triangles\":2000000,\"pixels\":4000000}},")
	TEXT("\"Min\":{\"sizeBytes\":300000000,\"counts\":{\"rows\":200,\"features\":120,\"triangles\":1000000,\"pixels\":1000000}}}");
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultLevelOfferTest,
	"MantlePlace.Vault.Levels.Offer",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultLevelOfferTest::RunTest(const FString& Parameters)
{
	TestEqual(TEXT("Three levels are offered, most to least"),
		FString::Join(FLogic::OfferedLevels(), TEXT(",")), FString(TEXT("Max,Med,Min")));
	TestTrue(TEXT("Med is a level"), FLogic::IsOfferedLevel(TEXT("Med")));
	TestFalse(TEXT("A 2.x token is not a 3.0 level"), FLogic::IsOfferedLevel(TEXT("MED")));
	TestFalse(TEXT("RAW is not offered"), FLogic::IsOfferedLevel(TEXT("RAW")));

	// A 3.0 row: the recommendation and each level's size and counts, verbatim.
	{
		TArray<FMantlePlaceVaultItem> Items;
		TArray<FString> Warnings;
		FString Error;
		TestTrue(TEXT("A 3.0 row parses"), FLogic::ParseListResponse(
			ListWithRow(FString(TEXT("{\"version\":\"3.0\",\"recommendedLevel\":\"Med\",")) + LevelsJson + TEXT(",\"extras\":[]}")),
			Items, Error, &Warnings));
		if (!TestEqual(TEXT("One row"), Items.Num(), 1))
		{
			return false;
		}
		const FMantlePlaceVaultItem& Item = Items[0];
		TestTrue(TEXT("The row offers levels"), Item.bHasLevels);
		TestEqual(TEXT("The recommendation is the platform's"), Item.RecommendedLevel, FString(TEXT("Med")));
		TestEqual(TEXT("No warning"), Warnings.Num(), 0);
		if (TestEqual(TEXT("Max, Med and Min"), Item.Levels.Num(), 3))
		{
			TestEqual(TEXT("In order: Max"), Item.Levels[0].Level, FString(TEXT("Max")));
			TestEqual(TEXT("In order: Med"), Item.Levels[1].Level, FString(TEXT("Med")));
			TestEqual(TEXT("In order: Min"), Item.Levels[2].Level, FString(TEXT("Min")));
			TestEqual(TEXT("Med's size, verbatim"), Item.Levels[1].SizeBytes, static_cast<int64>(1400000000));
			TestEqual(TEXT("Max's size above 32 bits"), Item.Levels[0].SizeBytes, static_cast<int64>(4000000000));
			TestEqual(TEXT("Med's rows"), Item.Levels[1].Rows, static_cast<int64>(400));
			TestEqual(TEXT("Med's features"), Item.Levels[1].Features, static_cast<int64>(120));
			TestEqual(TEXT("Med's triangles"), Item.Levels[1].Triangles, static_cast<int64>(2000000));
			TestEqual(TEXT("Med's pixels"), Item.Levels[1].Pixels, static_cast<int64>(4000000));
		}
		TestEqual(TEXT("The 2.x members are read as before"), Item.SizeBytes, static_cast<int64>(900));
	}

	// A 2.x row: no offer, no warning, nothing changed.
	{
		TArray<FMantlePlaceVaultItem> Items;
		TArray<FString> Warnings;
		FString Error;
		FLogic::ParseListResponse(ListWithRow(FString()), Items, Error, &Warnings);
		TestTrue(TEXT("A 2.x row offers no levels"), Items.Num() == 1 && !Items[0].bHasLevels && Items[0].Levels.Num() == 0);
		TestEqual(TEXT("A 2.x row warns of nothing"), Warnings.Num(), 0);
	}

	// An offer missing a level's size is not half-read: the row stays, as a 2.x row, and a warning says why.
	{
		TArray<FMantlePlaceVaultItem> Items;
		TArray<FString> Warnings;
		FString Error;
		FLogic::ParseListResponse(ListWithRow(TEXT(
			"{\"version\":\"3.0\",\"recommendedLevel\":\"Max\",\"levels\":{\"Max\":{\"sizeBytes\":10},\"Med\":{\"sizeBytes\":5}}}")),
			Items, Error, &Warnings);
		TestEqual(TEXT("The row is kept"), Items.Num(), 1);
		TestTrue(TEXT("An unreadable offer offers no levels"), Items.Num() == 1 && !Items[0].bHasLevels && Items[0].Levels.Num() == 0);
		TestEqual(TEXT("And says so"), Warnings.Num(), 1);
	}

	// No recommendation, or one not a level: nothing is preselected, and the levels are still offered.
	for (const TCHAR* Recommended : { TEXT(""), TEXT(",\"recommendedLevel\":\"MAX\"") })
	{
		TArray<FMantlePlaceVaultItem> Items;
		TArray<FString> Warnings;
		FString Error;
		FLogic::ParseListResponse(
			ListWithRow(FString(TEXT("{\"version\":\"3.0\"")) + Recommended + TEXT(",") + LevelsJson + TEXT("}")),
			Items, Error, &Warnings);
		TestTrue(TEXT("Without a recommendation the levels are still offered"), Items.Num() == 1 && Items[0].bHasLevels);
		TestTrue(TEXT("And none is recommended"), Items.Num() == 1 && Items[0].RecommendedLevel.IsEmpty());
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultLevelRequestTest,
	"MantlePlace.Vault.Levels.Request",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultLevelRequestTest::RunTest(const FString& Parameters)
{
	// The 3.0 request names the files this host reads and one level, in the published description's shape.
	const TArray<FString> Files = { TEXT("Elevation/Elevation.png"), TEXT("Imagery/Imagery.png") };
	TestEqual(TEXT("3.0 request, exact bytes"),
		FLogic::BuildLevelDownloadBody(TEXT("Med"), Files),
		FString(TEXT("{\"format\":\"bundle\",\"mpb3\":{\"version\":\"3.0\",\"level\":\"Med\","
			"\"files\":[\"Elevation/Elevation.png\",\"Imagery/Imagery.png\"]}}")));
	TestFalse(TEXT("A 3.0 request never carries a view"),
		FLogic::BuildLevelDownloadBody(TEXT("Min"), Files).Contains(TEXT("\"view\"")));

	// A 2.x order keeps its request: the view, unchanged.
	TestEqual(TEXT("2.x request unchanged"),
		FLogic::BuildViewDownloadBody(),
		FString(TEXT("{\"format\":\"bundle\",\"view\":{\"host\":\"unreal\",\"levels\":{\"*\":\"MAX\"}}}")));

	// The answer: the link, held as this level's download against its own stated length and digest.
	FMantlePlacePresignedDownload Download;
	FString Error;
	TestTrue(TEXT("A 3.0 answer parses"), FLogic::ParseLevelDownloadResponse(
		TEXT("{\"url\":\"https://r2.example/l.zip\",\"expiresAt\":\"2026-11-16T00:00:00Z\",\"sizeBytes\":1400000000,\"manifestSha256\":\"cd34\"}"),
		TEXT("Med"), Download, Error));
	TestEqual(TEXT("It holds the level asked for"), Download.Level, FString(TEXT("Med")));
	TestTrue(TEXT("It is held against its own record, not the listing's"), Download.bIsView);
	TestFalse(TEXT("A level download is never partial"), Download.bPartial);
	TestTrue(TEXT("Its stated length is read"), Download.bHasSizeBytes && Download.SizeBytes == 1400000000);
	TestEqual(TEXT("Its manifest digest is read"), Download.ManifestSha256, FString(TEXT("cd34")));

	FMantlePlacePresignedDownload Refused;
	TestFalse(TEXT("A refusal is not a link"), FLogic::ParseLevelDownloadResponse(
		TEXT("{\"error\":\"3.0 is not served yet\",\"code\":\"mpb3_not_yet_served\"}"), TEXT("Med"), Refused, Error));
	TestTrue(TEXT("And names the platform's words"), Error.Contains(TEXT("not served yet")));

	// A 2.x answer read the 2.x way names no level.
	FMantlePlacePresignedDownload Whole;
	FLogic::ParseDownloadResponse(TEXT("{\"url\":\"https://r2.example/w.zip\"}"), Whole, Error);
	TestTrue(TEXT("A 2.x download holds no level"), Whole.Level.IsEmpty());
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
