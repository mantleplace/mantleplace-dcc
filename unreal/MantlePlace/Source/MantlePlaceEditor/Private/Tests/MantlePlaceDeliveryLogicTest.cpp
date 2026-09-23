// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceDeliveryLogic.h"
#include "MantlePlaceImportManifest.h"
#include "Tests/MantlePlaceConformanceCorpus.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceDeliveryLogicTest,
    "MantlePlace.Import.DeliveryLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
FMantlePlaceDeliveryFacts Declared(const TCHAR* UnitSystem, const TCHAR* LinearUnit, int32 Epsg)
{
	FMantlePlaceDeliveryFacts Facts;
	Facts.bDeclared = true;
	Facts.UnitSystem = UnitSystem;
	Facts.LinearUnit = LinearUnit;
	Facts.bHasHorizontalEpsg = Epsg != 0;
	Facts.HorizontalEpsg = Epsg;
	return Facts;
}

/** The corpus's reference manifest with `delivery` replaced by `DeliveryJson` (or removed, when
 *  it is empty), parsed by the real reader. The reference fixture is what makes Parse accept the
 *  document at all; nothing here asserts anything else about it. */
FMantlePlaceVaultManifest ParseReferenceWith(FAutomationTestBase& T, const FString& DeliveryJson)
{
	const FString Path = FPaths::Combine(
	    MantlePlaceConformanceCorpus::FindCorpusRoot(), TEXT("manifest"), TEXT("full.json"));
	TSharedPtr<FJsonObject> Root;
	FString Text;
	if (!T.TestTrue(TEXT("the reference manifest loads from the corpus"),
	        MantlePlaceConformanceCorpus::Detail::LoadJsonObject(Path, Root, Text)))
	{
		return FMantlePlaceVaultManifest();
	}

	Root->RemoveField(TEXT("delivery"));
	if (!DeliveryJson.IsEmpty())
	{
		TSharedPtr<FJsonObject> Delivery;
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(DeliveryJson);
		T.TestTrue(TEXT("the delivery block is JSON"), FJsonSerializer::Deserialize(Reader, Delivery));
		Root->SetObjectField(TEXT("delivery"), Delivery);
	}

	FString Json;
	const TSharedRef<TJsonWriter<TCHAR>> Writer = TJsonWriterFactory<TCHAR>::Create(&Json);
	FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);

	FString Error;
	const FMantlePlaceVaultManifest Manifest = MantlePlaceImportManifest::Parse(Json, Error);
	T.TestTrue(FString::Printf(TEXT("the reference manifest parses (%s)"), *Error), Manifest.bValid);
	return Manifest;
}
} // namespace

bool FMantlePlaceDeliveryLogicTest::RunTest(const FString& Parameters)
{
	const FString FixedFrameLine =
	    TEXT("Unreal content: metres, engine-native. The delivered GIS and CAD files are in feet.");

	// --- A metric order: one line, and nothing about this host's frame, which is the delivery's ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("metric"), TEXT("m"), 32613));
		TestEqual(TEXT("metric: one line"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("metric: the shared words"), Lines[0], FString(TEXT("Bundle delivery: Metric · metres · EPSG:32613")));
		}
	}

	// --- An imperial order on a US-survey-foot State Plane grid: both lines, in that order ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("imperial"), TEXT("ftUS"), 6543));
		TestEqual(TEXT("sp_ftus: two lines"), Lines.Num(), 2);
		if (Lines.Num() == 2)
		{
			TestEqual(TEXT("sp_ftus: the shared words"), Lines[0], FString(TEXT("Bundle delivery: Imperial · US survey feet · EPSG:6543")));
			TestEqual(TEXT("sp_ftus: this host's content is metric"), Lines[1], FixedFrameLine);
		}
	}

	// --- The international foot is a different unit, and is named as one ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("imperial"), TEXT("ft"), 2223));
		TestEqual(TEXT("sp_ft: two lines"), Lines.Num(), 2);
		if (Lines.Num() == 2)
		{
			TestEqual(TEXT("sp_ft: the shared words"), Lines[0], FString(TEXT("Bundle delivery: Imperial · international feet · EPSG:2223")));
		}
	}

	// --- The `local_ft` tier names no CRS, and the line says so rather than inventing one ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("imperial"), TEXT("ft"), 0));
		TestEqual(TEXT("local_ft: two lines"), Lines.Num(), 2);
		if (Lines.Num() == 2)
		{
			TestEqual(TEXT("local_ft: no delivery CRS"), Lines[0], FString(TEXT("Bundle delivery: Imperial · international feet · no delivery CRS")));
		}
	}

	// --- A bundle built before the block existed says nothing, rather than guessing metric ---
	{
		TestEqual(TEXT("no delivery block: no lines"), FMantlePlaceDeliveryLogic::DescribeDelivery(FMantlePlaceDeliveryFacts()).Num(), 0);
	}

	// --- A value this host has no word for is shown as published, and is not taken for feet ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("nautical"), TEXT("fathom"), 9999));
		TestEqual(TEXT("unknown values: one line, no fixed-frame guess"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("unknown values: verbatim"), Lines[0], FString(TEXT("Bundle delivery: nautical · fathom · EPSG:9999")));
		}
	}

	// --- An imperial order whose files' unit has no word here is not said to be in feet ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT("imperial"), TEXT("yd"), 6543));
		TestEqual(TEXT("imperial, unknown unit: one line, no feet claim"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("imperial, unknown unit: verbatim"), Lines[0], FString(TEXT("Bundle delivery: Imperial · yd · EPSG:6543")));
		}
	}

	// --- A declared block missing a field it requires says which, rather than a blank segment ---
	{
		const TArray<FString> Lines = FMantlePlaceDeliveryLogic::DescribeDelivery(Declared(TEXT(""), TEXT(""), 32613));
		TestEqual(TEXT("missing values: one line"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("missing values: not stated"), Lines[0], FString(TEXT("Bundle delivery: not stated · not stated · EPSG:32613")));
		}
	}

	// --- The reader carries the block verbatim, and a null CRS is no CRS ---
	{
		const FMantlePlaceVaultManifest Imperial = ParseReferenceWith(*this,
		    TEXT("{\"unit_system\":\"imperial\",\"tier\":\"sp_ftus\",\"linear_unit\":\"ftUS\",\"horizontal_epsg\":6543}"));
		TestTrue(TEXT("parse: declared"), Imperial.Delivery.bDeclared);
		TestEqual(TEXT("parse: unit system verbatim"), Imperial.Delivery.UnitSystem, FString(TEXT("imperial")));
		TestEqual(TEXT("parse: linear unit verbatim"), Imperial.Delivery.LinearUnit, FString(TEXT("ftUS")));
		TestTrue(TEXT("parse: has a CRS"), Imperial.Delivery.bHasHorizontalEpsg);
		TestEqual(TEXT("parse: the CRS"), Imperial.Delivery.HorizontalEpsg, 6543);
		TestEqual(TEXT("parse: the georeference is still this block's own"), Imperial.Epsg, 32613);

		const FMantlePlaceVaultManifest Local = ParseReferenceWith(*this,
		    TEXT("{\"unit_system\":\"imperial\",\"tier\":\"local_ft\",\"linear_unit\":\"ft\",\"horizontal_epsg\":null}"));
		TestTrue(TEXT("parse local_ft: declared"), Local.Delivery.bDeclared);
		TestFalse(TEXT("parse local_ft: a null CRS is no CRS"), Local.Delivery.bHasHorizontalEpsg);

		const FMantlePlaceVaultManifest Absent = ParseReferenceWith(*this, FString());
		TestFalse(TEXT("parse: no block, not declared"), Absent.Delivery.bDeclared);
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
