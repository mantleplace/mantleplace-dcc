// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceVaultRowView.h"
#include "MantlePlaceVaultTypes.h"

namespace
{
	/** Build a vault item with just the fields the row-view derivation reads. */
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

	// Brand accents as they display on screen (sRGB hex -> linear, the same conversion the impl uses).
	const FLinearColor White(FColor(0xFF, 0xFF, 0xFF)); // ready (the calm default)
	const FLinearColor Water(FColor(0x09, 0x95, 0xB5)); // processing (live / in-flight)
	const FLinearColor Zinc4(FColor(0xA1, 0xA1, 0xAA)); // refunded
	const FLinearColor Red4(FColor(0xFF, 0x64, 0x67));  // failed
	const FLinearColor Zinc5(FColor(0x71, 0x71, 0x7A)); // unknown
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceVaultRowViewTest,
	"MantlePlace.Vault.RowView",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceVaultRowViewTest::RunTest(const FString& Parameters)
{
	// The list shows only Available (succeeded) bundles, and the sole action is a uniform "Import"
	// (a BASE bundle materializes-then-imports transparently). No "GENERATE + IMPORT" / "TRY AGAIN"
	// wording, and no per-row reason line - advanced/failed handling lives on the website.

	// 1. Available + Unreal tier (ships glb), idle -> "Import", enabled, no reason.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb"), TEXT("geotiff")}), /*bBusy*/ false);
		TestEqual(TEXT("available/unreal status label"), V.StatusLabel, FString(TEXT("ready")));
		TestTrue(TEXT("available/unreal status color=white"), V.StatusColor.Equals(White, 1e-4f));
		TestEqual(TEXT("available/unreal tier"), V.TierLabel, FString(TEXT("Unreal")));
		TestEqual(TEXT("available/unreal primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("available/unreal enabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("available/unreal no reason"), V.bShowReason);
	}

	// 2. Available + Base tier (formats but no glb) -> uniform "Import", enabled (materialize is transparent).
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("geotiff"), TEXT("pmtiles")}), false);
		TestEqual(TEXT("available/base tier"), V.TierLabel, FString(TEXT("Base")));
		TestEqual(TEXT("available/base primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("available/base enabled"), V.bPrimaryEnabled);
		TestEqual(TEXT("available/base status label"), V.StatusLabel, FString(TEXT("ready")));
	}

	// 3. Busy disables every row (only one import at a time).
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("geotiff")}), /*bBusy*/ true);
		TestFalse(TEXT("busy disables primary"), V.bPrimaryEnabled);
	}

	// 4. RefreshPending (filtered from the list, but the pure derivation stays total) -> "processing"
	//    tint, disabled Import, no reason.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::RefreshPending, {TEXT("glb")}), false);
		TestEqual(TEXT("refresh-pending label"), V.StatusLabel, FString(TEXT("processing")));
		TestTrue(TEXT("refresh-pending color=water"), V.StatusColor.Equals(Water, 1e-4f));
		TestEqual(TEXT("refresh-pending primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestFalse(TEXT("refresh-pending disabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("refresh-pending no reason"), V.bShowReason);
	}

	// 5. Refunded -> zinc tint, disabled.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Refunded, {TEXT("glb")}), false);
		TestEqual(TEXT("refunded label"), V.StatusLabel, FString(TEXT("refunded")));
		TestTrue(TEXT("refunded color=zinc400"), V.StatusColor.Equals(Zinc4, 1e-4f));
		TestFalse(TEXT("refunded disabled"), V.bPrimaryEnabled);
	}

	// 6. Failed -> red tint, disabled Import, NO "TRY AGAIN".
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Failed, {TEXT("glb")}), false);
		TestEqual(TEXT("failed label"), V.StatusLabel, FString(TEXT("failed")));
		TestTrue(TEXT("failed color=red"), V.StatusColor.Equals(Red4, 1e-4f));
		TestEqual(TEXT("failed primary is Import (no try again)"), V.PrimaryLabel, FString(TEXT("Import")));
		TestFalse(TEXT("failed disabled"), V.bPrimaryEnabled);
		TestFalse(TEXT("failed no reason"), V.bShowReason);
	}

	// 7. Unknown status -> disabled, zinc-500 tint.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Unknown, {TEXT("glb")}), false);
		TestEqual(TEXT("unknown label"), V.StatusLabel, FString(TEXT("unknown")));
		TestTrue(TEXT("unknown color=zinc500"), V.StatusColor.Equals(Zinc5, 1e-4f));
		TestFalse(TEXT("unknown disabled"), V.bPrimaryEnabled);
	}

	// 8. Empty formats Available -> tier "Unknown", uniform "Import", enabled.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {}), false);
		TestEqual(TEXT("empty-formats tier"), V.TierLabel, FString(TEXT("Unknown")));
		TestEqual(TEXT("empty-formats primary"), V.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("empty-formats enabled"), V.bPrimaryEnabled);
	}

	// 9. Codename is capitalized per word.
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")}, TEXT("boston common")), false);
		TestEqual(TEXT("codename capitalized"), V.Codename, FString(TEXT("Boston Common")));
	}

	// 10. Meta line: "{area:.2f} km2 - {Mon d, yyyy}" (km2 = U+00B2, separator = U+00B7).
	{
		const FMantlePlaceVaultRowView V = BuildVaultRowView(
			MakeItem(EMantlePlaceVaultBundleStatus::Available, {TEXT("glb")}, TEXT("aoi"), 12.34,
				TEXT("2026-06-17T10:00:00Z")), false);
		const FString ExpectedMeta =
			FString::Printf(TEXT("12.34 km%c %c Jun 17, 2026"), (TCHAR)0x00B2, (TCHAR)0x00B7);
		TestEqual(TEXT("meta line"), V.MetaLine, ExpectedMeta);
	}

	// 11. FormatVaultBytes: non-positive -> "-"; scales B/KB/MB.
	{
		TestEqual(TEXT("bytes 0"), FormatVaultBytes(0), FString(TEXT("-")));
		TestEqual(TEXT("bytes negative"), FormatVaultBytes(-5), FString(TEXT("-")));
		TestEqual(TEXT("bytes 512"), FormatVaultBytes(512), FString(TEXT("512 B")));
		TestEqual(TEXT("bytes 1536"), FormatVaultBytes(1536), FString(TEXT("1.5 KB")));
		TestEqual(TEXT("bytes 1 MB"), FormatVaultBytes(1024 * 1024), FString(TEXT("1.0 MB")));
	}

	// 12. Detail view: high-level fields, uniform Import, artifacts, layers, order line.
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

		const FMantlePlaceVaultDetailView D = BuildVaultDetailView(Item, /*bBusy*/ false);
		TestEqual(TEXT("detail codename"), D.Codename, FString(TEXT("Boston Common")));
		TestEqual(TEXT("detail order line"), D.OrderLine, FString(TEXT("order #abcdef012345")));
		TestEqual(TEXT("detail status"), D.StatusLabel, FString(TEXT("ready")));
		TestEqual(TEXT("detail tier"), D.TierLabel, FString(TEXT("Unreal")));
		TestEqual(TEXT("detail primary"), D.PrimaryLabel, FString(TEXT("Import")));
		TestTrue(TEXT("detail enabled"), D.bPrimaryEnabled);
		TestEqual(TEXT("detail size"), D.SizeText, FString(TEXT("13.0 MB")));
		// "Delivered as" = dotted format strip over the download formats (web formatStripLabel).
		const FString ExpectedDelivered =
			FString::Printf(TEXT(".glb %c .geotiff"), (TCHAR)0x00B7 /* middot */);
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

	// 13. Detail view for a non-Available bundle -> Import disabled.
	{
		const FMantlePlaceVaultDetailView D = BuildVaultDetailView(
			MakeItem(EMantlePlaceVaultBundleStatus::Failed, {TEXT("glb")}), false);
		TestFalse(TEXT("detail non-available import disabled"), D.bPrimaryEnabled);
	}

	// 14. Format explainers (web format-explainers.ts parity) + delivered-as strip edge cases.
	{
		TestEqual(TEXT("glb label"), GetFormatExplainer(TEXT("glb")).Label, FString(TEXT(".glb")));
		TestEqual(TEXT("cog opens-with"), GetFormatExplainer(TEXT("cog")).OpensWith, FString(TEXT("QGIS, ArcGIS, GDAL")));
		TestEqual(TEXT("explainer is case-insensitive"), GetFormatExplainer(TEXT("PMTILES")).Label, FString(TEXT(".pmtiles")));
		// Unknown format: dotted label, no invented tool copy.
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

#endif // WITH_DEV_AUTOMATION_TESTS
