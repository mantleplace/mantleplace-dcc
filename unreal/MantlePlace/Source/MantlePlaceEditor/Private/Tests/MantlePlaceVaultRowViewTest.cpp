// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"

namespace
{
	FMantlePlaceVaultItem MakeItem(
		EMantlePlaceVaultBundleStatus Status,
		const TArray<FString>& Formats,
		const FString& AoiLabel = TEXT("aoi"),
		double AreaKm2 = 1.0,
		const FString& CreatedAt = TEXT("2026-06-17T10:00:00Z"))
	{
		FMantlePlaceVaultItem Item;
		Item.Status = Status;
		Item.Formats = Formats;
		Item.AoiLabel = AoiLabel;
		Item.AreaKm2 = AreaKm2;
		Item.CreatedAt = CreatedAt;
		return Item;
	}

	const FLinearColor White(FColor(0xFF, 0xFF, 0xFF));
	const FLinearColor Water(FColor(0x09, 0x95, 0xB5));
	const FLinearColor Zinc4(FColor(0xA1, 0xA1, 0xAA));
	const FLinearColor Red4(FColor(0xFF, 0x64, 0x67));
	const FLinearColor Zinc5(FColor(0x71, 0x71, 0x7A));
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultRowViewTest,
	"MantlePlace.Vault.RowView",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultRowViewTest::RunTest(const FString& Parameters)
{
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb"), TEXT("geotiff")}), false);
		TestEqual(TEXT("available/unreal status label"), V.StatusLabel, FString(TEXT("ready")));
		TestTrue(TEXT("available/unreal status color=white"), V.StatusColor.Equals(White, 1e-4f));
		TestEqual(TEXT("available/unreal tier"), V.TierLabel, FString(TEXT("Unreal")));
		TestEqual(TEXT("available/unreal primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("available/unreal enabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("available/unreal no reason"), V.bShowReason);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("geotiff"), TEXT("pmtiles")}), false);
		TestEqual(TEXT("available/base tier"), V.TierLabel, FString(TEXT("Base")));
		TestEqual(TEXT("available/base primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("available/base enabled"), V.bPrimaryEnabled);
		TestEqual(TEXT("available/base status label"), V.StatusLabel, FString(TEXT("ready")));
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("geotiff")}), true);
		TestFalse(TEXT("busy disables primary"), V.bPrimaryEnabled);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::RefreshPending, {TEXT("glb")}), false);
		TestEqual(TEXT("refresh-pending label"), V.StatusLabel, FString(TEXT("processing")));
		TestTrue(TEXT("refresh-pending color=water"), V.StatusColor.Equals(Water, 1e-4f));
		TestEqual(TEXT("refresh-pending primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestFalse(TEXT("refresh-pending disabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("refresh-pending no reason"), V.bShowReason);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Refunded, {TEXT("glb")}), false);
		TestEqual(TEXT("refunded label"), V.StatusLabel, FString(TEXT("refunded")));
		TestTrue(TEXT("refunded color=zinc400"), V.StatusColor.Equals(Zinc4, 1e-4f));
		TestFalse(TEXT("refunded disabled"), V.bPrimaryEnabled);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Failed, {TEXT("glb")}), false);
		TestEqual(TEXT("failed label"), V.StatusLabel, FString(TEXT("failed")));
		TestTrue(TEXT("failed color=red"), V.StatusColor.Equals(Red4, 1e-4f));
		TestEqual(TEXT("failed primary is Import (no try again)"), V.PrimaryLabel, FString(TEXT("Import")));
		TestFalse(TEXT("failed disabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("failed no reason"), V.bShowReason);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Unknown, {TEXT("glb")}), false);
		TestEqual(TEXT("unknown label"), V.StatusLabel, FString(TEXT("unknown")));
		TestTrue(TEXT("unknown color=zinc500"), V.StatusColor.Equals(Zinc5, 1e-4f));
		TestFalse(TEXT("unknown disabled"), V.bPrimaryEnabled);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {}), false);
		TestEqual(TEXT("empty-formats tier"), V.TierLabel, FString(TEXT("Unknown")));
		TestEqual(TEXT("empty-formats primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("empty-formats enabled"), V.bPrimaryEnabled);
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")}, TEXT("boston common")), false);
		TestEqual(TEXT("codename capitalized"), V.Codename, FString(TEXT("Boston Common")));
	}

	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")}, TEXT("aoi"), 12.34,
				TEXT("2026-06-17T10:00:00Z")), false);
		const FString ExpectedMeta =
			FString::Printf(TEXT("12.34 km%c %c Jun 17, 2026"), (TCHAR)0x00B2, (TCHAR)0x00B7);
		TestEqual(TEXT("meta line"), V.MetaLine, ExpectedMeta);
	}

	{
		TestEqual(TEXT("bytes 0"), FormatVaultBytes(0), FString(TEXT("-")));
		TestEqual(TEXT("bytes negative"), FormatVaultBytes(-5), FString(TEXT("-")));
		TestEqual(TEXT("bytes 512"), FormatVaultBytes(512), FString(TEXT("512 B")));
		TestEqual(TEXT("bytes 1536"), FormatVaultBytes(1536), FString(TEXT("1.5 KB")));
		TestEqual(TEXT("bytes 1 MB"), FormatVaultBytes(1024 * 1024), FString(TEXT("1.0 MB")));
	}

	{
		FMantlePlaceVaultItem Item = MakeItem(
			EMantlePlaceVaultBundleStatus::Available, {TEXT("glb"), TEXT("geotiff")}, TEXT("boston common"), 12.34);
		Item.OrderId = TEXT("abcdef0123456789");
		Item.bHasSizeBytes = true;
		Item.SizeBytes = 13 * 1024 * 1024;
		Item.bLayersKnown = true;
		Item.Layers.bImagery = true;
		Item.Layers.bElevation = true;
		FMantlePlaceVaultArtifact A0; A0.Format = TEXT("glb"); A0.ByteSize = 12 * 1024 * 1024;
		FMantlePlaceVaultArtifact A1; A1.Format = TEXT("geotiff"); A1.ByteSize = 512 * 1024;
		Item.DownloadFormats = {A0, A1};

		const FMantlePlaceVaultDetailView D = BuildVaultDetailView(Item, false);
		TestEqual(TEXT("detail codename"), D.Codename, FString(TEXT("Boston Common")));
		TestEqual(TEXT("detail order line"), D.OrderLine, FString(TEXT("order #abcdef012345")));
		TestEqual(TEXT("detail status"), D.StatusLabel, FString(TEXT("ready")));
		TestEqual(TEXT("detail tier"), D.TierLabel, FString(TEXT("Unreal")));
		TestEqual(TEXT("detail primary"), D.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("detail enabled"), D.bPrimaryEnabled);
		TestEqual(TEXT("detail size"), D.SizeText, FString(TEXT("13.0 MB")));
		const FString ExpectedDelivered =
			FString::Printf(TEXT(".glb %c .geotiff"), (TCHAR)0x00B7);
		TestEqual(TEXT("detail delivered-as strip"), D.DeliveredAsText, ExpectedDelivered);
		TestEqual(TEXT("detail artifacts count"), D.Artifacts.Num(), 2);
		TestEqual(TEXT("detail artifact 0 label (dotted)"), D.Artifacts[0].Label, FString(TEXT(".glb")));
		TestEqual(TEXT("detail artifact 0 opens-with"), D.Artifacts[0].OpensWith,
			FString(TEXT("Blender, Unreal Engine, three.js")));
		{
			const FString ExpectedSub =
				FString::Printf(TEXT("12.0 MB %c opens with Blender, Unreal Engine, three.js"), (TCHAR)0x00B7);
			TestEqual(TEXT("detail artifact 0 sub-line"), D.Artifacts[0].SubLine, ExpectedSub);
		}
		TestTrue(TEXT("detail layers known"), D.bLayersKnown);
		TestTrue(TEXT("detail imagery"), D.bImagery);
		TestTrue(TEXT("detail elevation"), D.bElevation);
		TestFalse(TEXT("detail basemap"), D.bBasemap);
	}

	{
		FMantlePlaceVaultItem Item = MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")});
		FMantlePlaceVaultExtra Brief; Brief.Id = TEXT("SiteBrief"); Brief.Name = TEXT("Site Brief"); Brief.SizeBytes = 2048;
		FMantlePlaceVaultExtra Plans; Plans.Id = TEXT("PlanSheets"); Plans.Name = TEXT("Plan sheets"); Plans.bLocked = true;
		FMantlePlaceVaultExtra Tiles; Tiles.Id = TEXT("TerrainTiles"); Tiles.Name = TEXT("Terrain tiles"); Tiles.bLocked = true;
		Item.Extras = {Brief, Plans, Tiles};

		const FMantlePlaceVaultDetailView Free = BuildVaultDetailView(Item, false);
		if (TestEqual(TEXT("a locked Extra is left out, so one Extra row"), Free.Extras.Num(), 1))
		{
			TestEqual(TEXT("the Site Brief row is named"), Free.Extras[0].Label, FString(TEXT("Site Brief")));
			TestEqual(TEXT("the Site Brief row shows its size"), Free.Extras[0].SubLine, FString(TEXT("2.0 KB")));
		}

		Item.Extras[1].bLocked = false;
		Item.Extras[2].bLocked = false;
		const FMantlePlaceVaultDetailView Unlocked = BuildVaultDetailView(Item, false);
		if (TestEqual(TEXT("an order that includes them lists all three"), Unlocked.Extras.Num(), 3))
		{
			TestEqual(TEXT("the plan sheets row is named"), Unlocked.Extras[1].Label, FString(TEXT("Plan sheets")));
		}

		TestEqual(TEXT("a 2.x bundle has no Extra rows"),
			BuildVaultDetailView(MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")}), false).Extras.Num(), 0);
	}

	{
		const FMantlePlaceVaultDetailView D = BuildVaultDetailView(
			MakeItem(EMantlePlaceVaultBundleStatus::Failed, {TEXT("glb")}), false);
		TestFalse(TEXT("detail non-available import disabled"), D.bPrimaryEnabled);
	}

	{
		TestEqual(TEXT("glb label"), GetFormatExplainer(TEXT("glb")).Label, FString(TEXT(".glb")));
		TestEqual(TEXT("cog opens-with"), GetFormatExplainer(TEXT("cog")).OpensWith, FString(TEXT("QGIS, ArcGIS, GDAL")));
		TestEqual(TEXT("explainer is case-insensitive"), GetFormatExplainer(TEXT("PMTILES")).Label, FString(TEXT(".pmtiles")));
		TestEqual(TEXT("unknown format dotted label"), GetFormatExplainer(TEXT("xyz")).Label, FString(TEXT(".xyz")));
		TestTrue(TEXT("unknown format has no opens-with"), GetFormatExplainer(TEXT("xyz")).OpensWith.IsEmpty());
		TestEqual(TEXT("empty format -> empty label"), GetFormatExplainer(TEXT("")).Label, FString());

		TestEqual(TEXT("delivered-as empty -> dash"), FormatDeliveredAs({}), FString(TEXT("-")));
		const FString ExpectedStrip = FString::Printf(TEXT(".glb %c .cog"), (TCHAR)0x00B7);
		TestEqual(TEXT("delivered-as strip joins dotted labels"),
			FormatDeliveredAs({TEXT("glb"), TEXT("cog")}), ExpectedStrip);
	}

	return true;
}

#endif
