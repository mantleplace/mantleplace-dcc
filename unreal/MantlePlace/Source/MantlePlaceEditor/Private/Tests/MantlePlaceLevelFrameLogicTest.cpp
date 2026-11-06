// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLevelFrameLogic.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceLevelFrameLogicTest,
    "MantlePlace.Import.LevelFrameLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
FMantlePlaceFrameOrigin MakeOrigin(const TCHAR* Crs, const TCHAR* Datum, double EastingM, double NorthingM)
{
	FMantlePlaceFrameOrigin Result;
	Result.Crs = Crs;
	Result.VerticalDatum = Datum;
	Result.EastingM = EastingM;
	Result.NorthingM = NorthingM;
	Result.bStated = true;
	return Result;
}

/** What the level holds once `Plan` has been imported under `Identity`: one entry per actor, so two here. */
void AddImport(TArray<FMantlePlaceLevelImportTag>& Level, const TCHAR* Identity, const FMantlePlaceFramePlan& Plan)
{
	for (int32 Actor = 0; Actor < 2; ++Actor)
	{
		FMantlePlaceLevelImportTag Tag;
		Tag.Identity = Identity;
		Tag.FrameValue = Plan.FrameValue;
		Level.Add(Tag);
	}
}

const TCHAR* const Utm13 = TEXT("EPSG:32613");
const TCHAR* const NavdGeoid18 = TEXT("NAVD88 (GEOID18)");
}

bool FMantlePlaceLevelFrameLogicTest::RunTest(const FString& Parameters)
{
	const FMantlePlaceFrameOrigin A = MakeOrigin(Utm13, NavdGeoid18, 441959.5, 4014372.5);
	// 1500 m east of A and 200 m south of it: an adjacent tile.
	const FMantlePlaceFrameOrigin B = MakeOrigin(Utm13, NavdGeoid18, 443459.5, 4014172.5);
	// 3000 m east of A and 100 m north of it: a third tile in the row.
	const FMantlePlaceFrameOrigin C = MakeOrigin(Utm13, NavdGeoid18, 444959.5, 4014472.5);

	// --- An empty level: the first import is the anchor, at the world origin ---------------------
	TArray<FMantlePlaceLevelImportTag> Level;
	const FMantlePlaceFramePlan First = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, Level);
	TestTrue(TEXT("empty level: anchor"), First.Outcome == EMantlePlaceFrameOutcome::Anchor);
	TestEqual(TEXT("anchor east offset"), First.OffsetEastM, 0.0, 1e-9);
	TestEqual(TEXT("anchor north offset"), First.OffsetNorthM, 0.0, 1e-9);
	TestFalse(TEXT("anchor: nothing to move"), First.HasOffset());
	TestTrue(TEXT("anchor: no warning"), First.Warning.IsEmpty());
	TestFalse(TEXT("anchor: tag value written"), First.FrameValue.IsEmpty());
	AddImport(Level, TEXT("orderA"), First);

	// --- A second, adjacent bundle: offset = its origin minus the anchor's -------------------------
	const FMantlePlaceFramePlan Second = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderB"), B, Level);
	TestTrue(TEXT("adjacent: offset"), Second.Outcome == EMantlePlaceFrameOutcome::Offset);
	TestEqual(TEXT("adjacent east"), Second.OffsetEastM, 1500.0, 1e-6);
	TestEqual(TEXT("adjacent north"), Second.OffsetNorthM, -200.0, 1e-6);
	TestTrue(TEXT("adjacent: has offset"), Second.HasOffset());
	TestTrue(TEXT("adjacent: no warning"), Second.Warning.IsEmpty());
	// The offset becomes UE cm through the one axis-mapping helper: North -> +X, East -> +Y.
	const FVector SecondCm = FMantlePlaceVaultManifest::ProjectedToUeCm(Second.OffsetEastM, Second.OffsetNorthM, 0.0);
	TestEqual(TEXT("adjacent X is north in cm"), SecondCm.X, -20000.0, 1e-6);
	TestEqual(TEXT("adjacent Y is east in cm"), SecondCm.Y, 150000.0, 1e-6);
	AddImport(Level, TEXT("orderB"), Second);

	// --- A third bundle lands against the FIRST, not against the second ----------------------------
	const FMantlePlaceFramePlan Third = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderC"), C, Level);
	TestTrue(TEXT("third: offset"), Third.Outcome == EMantlePlaceFrameOutcome::Offset);
	TestEqual(TEXT("third east, from the anchor"), Third.OffsetEastM, 3000.0, 1e-6);
	TestEqual(TEXT("third north, from the anchor"), Third.OffsetNorthM, 100.0, 1e-6);
	AddImport(Level, TEXT("orderC"), Third);

	// --- Re-import keeps each bundle's place ------------------------------------------------------
	{
		const FMantlePlaceFramePlan ReB = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderB"), B, Level);
		TestTrue(TEXT("re-import B: offset"), ReB.Outcome == EMantlePlaceFrameOutcome::Offset);
		TestEqual(TEXT("re-import B east"), ReB.OffsetEastM, 1500.0, 1e-6);
		TestEqual(TEXT("re-import B north"), ReB.OffsetNorthM, -200.0, 1e-6);

		const FMantlePlaceFramePlan ReA = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, Level);
		TestFalse(TEXT("re-import the anchor bundle: offset zero"), ReA.HasOffset());
		TestEqual(TEXT("re-import A east"), ReA.OffsetEastM, 0.0, 1e-6);
		TestEqual(TEXT("re-import A north"), ReA.OffsetNorthM, 0.0, 1e-6);
		TestTrue(TEXT("re-import A: still placed on the frame"), ReA.Outcome == EMantlePlaceFrameOutcome::Offset);
		TestTrue(TEXT("re-import A: no warning"), ReA.Warning.IsEmpty());

		// After the anchor bundle is re-imported, a new bundle still lands against the same point.
		TArray<FMantlePlaceLevelImportTag> AfterA = Level;
		AfterA.RemoveAll([](const FMantlePlaceLevelImportTag& Tag) { return Tag.Identity == TEXT("orderA"); });
		AddImport(AfterA, TEXT("orderA"), ReA);
		const FMantlePlaceFramePlan ThirdAgain = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderC"), C, AfterA);
		TestEqual(TEXT("anchor survives its own re-import"), ThirdAgain.OffsetEastM, 3000.0, 1e-6);
	}

	// --- The anchor outlives the anchor bundle while any placed import remains ---------------------
	{
		TArray<FMantlePlaceLevelImportTag> WithoutA = Level;
		WithoutA.RemoveAll([](const FMantlePlaceLevelImportTag& Tag) { return Tag.Identity == TEXT("orderA"); });
		const FMantlePlaceFramePlan ReA = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, WithoutA);
		TestEqual(TEXT("anchor bundle deleted, then re-imported: back at zero"), ReA.OffsetEastM, 0.0, 1e-6);
		TestEqual(TEXT("... north"), ReA.OffsetNorthM, 0.0, 1e-6);
		const FMantlePlaceFramePlan ReC = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderC"), C, WithoutA);
		TestEqual(TEXT("anchor kept by the remaining imports"), ReC.OffsetEastM, 3000.0, 1e-6);
	}

	// --- The anchor clears when no Mantle Place import is left ------------------------------------
	{
		const FMantlePlaceFramePlan Fresh = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderB"), B, TArray<FMantlePlaceLevelImportTag>());
		TestTrue(TEXT("no import left: the next one is the anchor"), Fresh.Outcome == EMantlePlaceFrameOutcome::Anchor);
		TestFalse(TEXT("... at zero"), Fresh.HasOffset());

		// Re-importing the level's only import is also an anchor: nothing else to measure against.
		const FMantlePlaceFramePlan Only = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, [&Level]
			{
				TArray<FMantlePlaceLevelImportTag> OnlyA;
				for (const FMantlePlaceLevelImportTag& Tag : Level)
				{
					if (Tag.Identity == TEXT("orderA")) { OnlyA.Add(Tag); }
				}
				return OnlyA;
			}());
		TestTrue(TEXT("sole import re-imported: anchor"), Only.Outcome == EMantlePlaceFrameOutcome::Anchor);

		// B alone, re-imported, keeps its offset: the anchor bundle A is gone, B's own tag is the witness.
		const FMantlePlaceFramePlan PlanB = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderB"), B, Level);
		TArray<FMantlePlaceLevelImportTag> OnlyB;
		AddImport(OnlyB, TEXT("orderB"), PlanB);
		const FMantlePlaceFramePlan ReOnlyB = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderB"), B, OnlyB);
		TestTrue(TEXT("B alone, re-imported, keeps its offset: offset"), ReOnlyB.Outcome == EMantlePlaceFrameOutcome::Offset);
		TestEqual(TEXT("B alone, re-imported, keeps its offset: east"), ReOnlyB.OffsetEastM, 1500.0, 1e-6);
		TestEqual(TEXT("B alone, re-imported, keeps its offset: north"), ReOnlyB.OffsetNorthM, -200.0, 1e-6);
	}

	// --- Every refusal imports centred, with ONE warning naming the reason --------------------------
	{
		const auto ExpectCentred = [this, &Level](const TCHAR* What, const FMantlePlaceFrameOrigin& Other,
											  EMantlePlaceFrameRefusal Reason, const TCHAR* WarningHas)
		{
			const FMantlePlaceFramePlan Plan = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderX"), Other, Level);
			TestTrue(FString::Printf(TEXT("%s: centred"), What), Plan.Outcome == EMantlePlaceFrameOutcome::Centred);
			TestTrue(FString::Printf(TEXT("%s: reason"), What), Plan.Refusal == Reason);
			TestFalse(FString::Printf(TEXT("%s: no offset"), What), Plan.HasOffset());
			TestEqual(FString::Printf(TEXT("%s: zero east"), What), Plan.OffsetEastM, 0.0, 1e-9);
			TestEqual(FString::Printf(TEXT("%s: zero north"), What), Plan.OffsetNorthM, 0.0, 1e-9);
			TestTrue(FString::Printf(TEXT("%s: warning names the reason"), What), Plan.Warning.Contains(WarningHas));
			TestTrue(FString::Printf(TEXT("%s: one warning line"), What),
				Plan.Warning.StartsWith(TEXT("WARNING:")) && !Plan.Warning.Contains(TEXT("\n")));
			// A centred import is recorded as not placed, so it is never taken for the anchor.
			FMantlePlaceFrameRecord Decoded;
			TestTrue(FString::Printf(TEXT("%s: tag decodes"), What), FMantlePlaceLevelFrameLogic::DecodeFrame(Plan.FrameValue, Decoded));
			TestFalse(FString::Printf(TEXT("%s: recorded as not placed"), What), Decoded.bPlaced);
		};

		ExpectCentred(TEXT("different CRS"), MakeOrigin(TEXT("EPSG:32614"), NavdGeoid18, 700000.0, 4014372.5),
			EMantlePlaceFrameRefusal::CrsDiffers, TEXT("EPSG:32614"));
		ExpectCentred(TEXT("different datum"), MakeOrigin(Utm13, TEXT("EGM2008-orthometric"), 443459.5, 4014172.5),
			EMantlePlaceFrameRefusal::DatumDiffers, TEXT("EGM2008-orthometric"));
		ExpectCentred(TEXT("datum unstated"), MakeOrigin(Utm13, TEXT(""), 443459.5, 4014172.5),
			EMantlePlaceFrameRefusal::DatumDiffers, TEXT("(none stated)"));
		ExpectCentred(TEXT("too far east"), MakeOrigin(Utm13, NavdGeoid18, 441959.5 + 100001.0, 4014372.5),
			EMantlePlaceFrameRefusal::TooFar, TEXT("100000 m"));
		ExpectCentred(TEXT("too far south"), MakeOrigin(Utm13, NavdGeoid18, 441959.5, 4014372.5 - 250000.0),
			EMantlePlaceFrameRefusal::TooFar, TEXT("-250000 m north"));

		FMantlePlaceFrameOrigin Unstated;
		ExpectCentred(TEXT("no origin"), Unstated, EMantlePlaceFrameRefusal::OriginUnstated, TEXT("no georeference origin"));
		ExpectCentred(TEXT("no CRS"), MakeOrigin(TEXT(""), NavdGeoid18, 443459.5, 4014172.5),
			EMantlePlaceFrameRefusal::OriginUnstated, TEXT("no projected CRS"));
	}

	// --- The CRS is a string: equal text is the only match, however alike two zones look -----------
	{
		const FMantlePlaceFramePlan Lookalike = FMantlePlaceLevelFrameLogic::Plan(
			TEXT("orderX"), MakeOrigin(TEXT("epsg:32613"), NavdGeoid18, 443459.5, 4014172.5), Level);
		TestTrue(TEXT("a differently written CRS is a different CRS"), Lookalike.Outcome == EMantlePlaceFrameOutcome::Centred);
	}

	// --- The limit is inclusive: exactly 100 km offsets ---------------------------------------------
	{
		const FMantlePlaceFramePlan AtLimit = FMantlePlaceLevelFrameLogic::Plan(
			TEXT("orderX"), MakeOrigin(Utm13, NavdGeoid18, 441959.5 + 100000.0, 4014372.5), Level);
		TestTrue(TEXT("exactly at the limit: offset"), AtLimit.Outcome == EMantlePlaceFrameOutcome::Offset);
	}

	// --- A refused import is not an anchor, and does not disturb the real one ------------------------
	{
		TArray<FMantlePlaceLevelImportTag> Mixed = Level;
		const FMantlePlaceFramePlan Far = FMantlePlaceLevelFrameLogic::Plan(
			TEXT("orderFar"), MakeOrigin(TEXT("EPSG:32614"), NavdGeoid18, 700000.0, 4014372.5), Level);
		AddImport(Mixed, TEXT("orderFar"), Far);
		const FMantlePlaceFramePlan ReC = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderC"), C, Mixed);
		TestEqual(TEXT("anchor unchanged by a centred import"), ReC.OffsetEastM, 3000.0, 1e-6);

		// A level holding only a centred import and a legacy (untagged) one: the new import anchors,
		// and says the others are not on its frame.
		TArray<FMantlePlaceLevelImportTag> Unframed;
		AddImport(Unframed, TEXT("orderFar"), Far);
		FMantlePlaceLevelImportTag Legacy;
		Legacy.Identity = TEXT("orderLegacy");
		Unframed.Add(Legacy);
		const FMantlePlaceFramePlan Anchor = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, Unframed);
		TestTrue(TEXT("only unplaced imports left: anchor"), Anchor.Outcome == EMantlePlaceFrameOutcome::Anchor);
		TestTrue(TEXT("... and says two stay where they are"), Anchor.Note.Contains(TEXT("2 other")));
	}

	// --- Garbage in a tag is not a witness ----------------------------------------------------------
	{
		TArray<FMantlePlaceLevelImportTag> Junk;
		for (const TCHAR* Value : {TEXT("garbage"), TEXT("v9|placed|x|y|1|2|3|4"), TEXT("v1|placed|x|y|1|2|3"),
				 TEXT("v1|placed|x|y|a|2|3|4"), TEXT("v1|elsewhere|x|y|1|2|3|4")})
		{
			FMantlePlaceLevelImportTag Tag;
			Tag.Identity = TEXT("orderJunk");
			Tag.FrameValue = Value;
			Junk.Add(Tag);
		}
		const FMantlePlaceFramePlan Plan = FMantlePlaceLevelFrameLogic::Plan(TEXT("orderA"), A, Junk);
		TestTrue(TEXT("unreadable tags place nothing: the new import anchors"), Plan.Outcome == EMantlePlaceFrameOutcome::Anchor);
	}

	// --- The tag round-trips, separators and all ----------------------------------------------------
	{
		FMantlePlaceFrameRecord In;
		In.bPlaced = true;
		In.Origin = MakeOrigin(TEXT("EPSG:32613|odd%name"), TEXT("NAVD88 (GEOID18)"), 441959.5, 4014372.5);
		In.OffsetEastM = 1500.25;
		In.OffsetNorthM = -200.125;
		FMantlePlaceFrameRecord Out;
		TestTrue(TEXT("round-trip decodes"), FMantlePlaceLevelFrameLogic::DecodeFrame(FMantlePlaceLevelFrameLogic::EncodeFrame(In), Out));
		TestTrue(TEXT("placed"), Out.bPlaced);
		TestEqual(TEXT("crs"), Out.Origin.Crs, In.Origin.Crs);
		TestEqual(TEXT("datum"), Out.Origin.VerticalDatum, In.Origin.VerticalDatum);
		TestEqual(TEXT("easting"), Out.Origin.EastingM, In.Origin.EastingM, 1e-4);
		TestEqual(TEXT("northing"), Out.Origin.NorthingM, In.Origin.NorthingM, 1e-4);
		TestEqual(TEXT("offset east"), Out.OffsetEastM, In.OffsetEastM, 1e-4);
		TestEqual(TEXT("offset north"), Out.OffsetNorthM, In.OffsetNorthM, 1e-4);
	}

	// --- ToLevelFrame is horizontal only: heights are already absolute ------------------------------
	{
		FMantlePlaceVaultManifest Manifest;
		const FVector Local(1000.0, 2000.0, 300000.0);
		TestTrue(TEXT("no offset: unchanged"), Manifest.ToLevelFrame(Local).Equals(Local, 1e-9));
		Manifest.SetLevelOffsetM(1500.0, -200.0);
		const FVector Moved = Manifest.ToLevelFrame(Local);
		TestEqual(TEXT("X moves by north"), Moved.X, 1000.0 - 20000.0, 1e-6);
		TestEqual(TEXT("Y moves by east"), Moved.Y, 2000.0 + 150000.0, 1e-6);
		TestEqual(TEXT("Z never moves"), Moved.Z, 300000.0, 1e-9);
		TestTrue(TEXT("has an offset"), Manifest.HasLevelOffset());
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
