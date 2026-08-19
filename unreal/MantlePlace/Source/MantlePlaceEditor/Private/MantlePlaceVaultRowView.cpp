// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceVaultRowView.h"

#include "MantlePlaceVaultTypes.h"
#include "MantlePlaceVaultImportOrchestrator.h" // static row helpers: IsBundleIncomplete / GetBundleTierLabel
#include "MantlePlacePalette.h"

namespace
{
	/** Capitalize the first letter of each whitespace-separated word (mirrors the web's CSS `capitalize`). */
	FString CapitalizeWords(const FString& In)
	{
		FString Out = In;
		bool bAtWordStart = true;
		for (int32 i = 0; i < Out.Len(); ++i)
		{
			const TCHAR C = Out[i];
			if (FChar::IsWhitespace(C))
			{
				bAtWordStart = true;
			}
			else
			{
				if (bAtWordStart)
				{
					Out[i] = FChar::ToUpper(C);
				}
				bAtWordStart = false;
			}
		}
		return Out;
	}

	/** ISO-8601 -> "Jun 17, 2026"; falls back to the raw string if it can't be parsed. */
	FString FormatCreatedAt(const FString& Iso)
	{
		static const TCHAR* Months[] = {
			TEXT("Jan"), TEXT("Feb"), TEXT("Mar"), TEXT("Apr"), TEXT("May"), TEXT("Jun"),
			TEXT("Jul"), TEXT("Aug"), TEXT("Sep"), TEXT("Oct"), TEXT("Nov"), TEXT("Dec")
		};

		FDateTime When;
		if (!Iso.IsEmpty() && FDateTime::ParseIso8601(*Iso, When))
		{
			const int32 MonthIndex = FMath::Clamp(When.GetMonth(), 1, 12) - 1;
			return FString::Printf(TEXT("%s %d, %d"), Months[MonthIndex], When.GetDay(), When.GetYear());
		}
		return Iso;
	}

	/** "{area:.2f} km2" (km2 = U+00B2). */
	FString FormatExtent(double AreaKm2)
	{
		return FString::Printf(TEXT("%.2f km%c"), AreaKm2, (TCHAR)0x00B2 /* superscript two */);
	}

	/** "{area} km2 - {date}" ( - = U+00B7 middot); the shared meta sub-label. */
	FString FormatMetaLine(double AreaKm2, const FString& CreatedAt)
	{
		return FString::Printf(TEXT("%s %c %s"),
			*FormatExtent(AreaKm2), (TCHAR)0x00B7 /* middot */, *FormatCreatedAt(CreatedAt));
	}
}

FMantlePlaceVaultStatusView GetVaultStatusView(EMantlePlaceVaultBundleStatus Status)
{
	// Function-scope (not file/global) so palette names don't leak across the unity build (CPP-24) and
	// collide with same-named locals in a concatenated TU (e.g. the row-view test's White/Water).
	using namespace MantlePlacePalette;
	FMantlePlaceVaultStatusView View;
	switch (Status)
	{
	case EMantlePlaceVaultBundleStatus::Available:
		View.Label = TEXT("ready");
		View.Color = White(); // web: "ready" is the calm default - plain white, not an accent
		break;
	case EMantlePlaceVaultBundleStatus::RefreshPending:
		View.Label = TEXT("processing");
		View.Color = Water(); // web: cyan is reserved for the live / in-flight state
		break;
	case EMantlePlaceVaultBundleStatus::Refunded:
		View.Label = TEXT("refunded");
		View.Color = Zinc400();
		break;
	case EMantlePlaceVaultBundleStatus::Failed:
		View.Label = TEXT("failed");
		View.Color = Red400();
		break;
	case EMantlePlaceVaultBundleStatus::Unknown:
	default:
		View.Label = TEXT("unknown");
		View.Color = Zinc500();
		break;
	}
	return View;
}

FMantlePlaceFormatExplainer GetFormatExplainer(const FString& Format)
{
	// Mirror of the web app's format explainers (label + opensWith only; the editor doesn't
	// surface the "whatItIs" line). Keep this list in sync with that single source.
	static const TMap<FString, FMantlePlaceFormatExplainer> Explainers = {
		{ TEXT("glb"),     { TEXT(".glb"),     TEXT("Blender, Unreal Engine, three.js") } },
		{ TEXT("gltf"),    { TEXT(".gltf"),    TEXT("Blender, Unreal Engine, three.js") } },
		{ TEXT("usda"),    { TEXT(".usda"),    TEXT("Omniverse, usdview, Unreal Engine") } },
		{ TEXT("usdc"),    { TEXT(".usdc"),    TEXT("Omniverse, usdview, Unreal Engine") } },
		{ TEXT("fbx"),     { TEXT(".fbx"),     TEXT("3ds Max, Maya, Cinema 4D") } },
		{ TEXT("geotiff"), { TEXT(".geotiff"), TEXT("QGIS, ArcGIS, Global Mapper") } },
		{ TEXT("cog"),     { TEXT(".cog"),     TEXT("QGIS, ArcGIS, GDAL") } },
		{ TEXT("dwg"),     { TEXT(".dwg"),     TEXT("AutoCAD, Civil 3D, BricsCAD") } },
		{ TEXT("pmtiles"), { TEXT(".pmtiles"), TEXT("QGIS, MapLibre, Felt") } },
		{ TEXT("gpkg"),    { TEXT(".gpkg"),    TEXT("QGIS, ArcGIS, FME") } },
		{ TEXT("geojson"), { TEXT(".geojson"), TEXT("QGIS, ArcGIS, web maps") } },
		{ TEXT("shp"),     { TEXT(".shp"),     TEXT("QGIS, ArcGIS, AutoCAD Map 3D") } },
		{ TEXT("fgb"),     { TEXT(".fgb"),     TEXT("QGIS, GDAL, web maps") } },
	};

	const FString Key = Format.ToLower();
	if (const FMantlePlaceFormatExplainer* Found = Explainers.Find(Key))
	{
		return *Found;
	}
	// Unknown/future format: dotted label, no tool list (never invent copy).
	return { Key.IsEmpty() ? FString() : FString(TEXT(".")) + Key, FString() };
}

FString FormatDeliveredAs(const TArray<FString>& Formats)
{
	if (Formats.Num() == 0)
	{
		return TEXT("-");
	}
	TArray<FString> Labels;
	Labels.Reserve(Formats.Num());
	for (const FString& Format : Formats)
	{
		Labels.Add(GetFormatExplainer(Format).Label);
	}
	const FString Sep = FString::Printf(TEXT(" %c "), (TCHAR)0x00B7 /* middot */);
	return FString::Join(Labels, *Sep);
}

FString FormatVaultBytes(int64 Bytes)
{
	if (Bytes <= 0)
	{
		return TEXT("-");
	}
	static const TCHAR* Units[] = { TEXT("B"), TEXT("KB"), TEXT("MB"), TEXT("GB"), TEXT("TB") };
	double Value = static_cast<double>(Bytes);
	int32 Unit = 0;
	while (Value >= 1024.0 && Unit < UE_ARRAY_COUNT(Units) - 1)
	{
		Value /= 1024.0;
		++Unit;
	}
	// Whole number for bytes; one decimal for KB+.
	return Unit == 0
		? FString::Printf(TEXT("%d %s"), static_cast<int32>(Value), Units[Unit])
		: FString::Printf(TEXT("%.1f %s"), Value, Units[Unit]);
}

FMantlePlaceVaultRowView BuildVaultRowView(const FMantlePlaceVaultItem& Item, bool bBusy)
{
	FMantlePlaceVaultRowView View;

	// Identity.
	const FString Label = Item.AoiLabel.IsEmpty() ? Item.OrderId : Item.AoiLabel;
	View.Codename = CapitalizeWords(Label);
	View.MetaLine = FormatMetaLine(Item.AreaKm2, Item.CreatedAt);

	// Tier (Base needs Generate, Unreal is ready) - reuse the orchestrator's shared logic, don't duplicate it.
	View.TierLabel = UMantlePlaceVaultImportOrchestrator::GetBundleTierLabel(Item);

	// Status -> pill label + accent (shared helper).
	const FMantlePlaceVaultStatusView StatusView = GetVaultStatusView(Item.Status);
	View.StatusLabel = StatusView.Label;
	View.StatusColor = StatusView.Color;
	View.ReasonColor = StatusView.Color;

	// The list only ever shows Available (succeeded) bundles, so the action is a single, uniform "Import":
	// a BASE bundle transparently materializes-then-imports; an UNREAL bundle imports directly. There is
	// deliberately no "GENERATE + IMPORT" or "TRY AGAIN" wording - advanced/failed handling lives on the web.
	// Only one import runs at a time (bBusy) -> every row's action is disabled while busy. Non-Available
	// statuses are filtered out upstream; they resolve to a disabled Import here for totality.
	View.PrimaryLabel = TEXT("Import");
	View.bPrimaryEnabled = (Item.Status == EMantlePlaceVaultBundleStatus::Available) && !bBusy;
	View.bShowReason = false;

	return View;
}

FMantlePlaceVaultDetailView BuildVaultDetailView(const FMantlePlaceVaultItem& Item, bool bBusy)
{
	FMantlePlaceVaultDetailView View;

	const FString Label = Item.AoiLabel.IsEmpty() ? Item.OrderId : Item.AoiLabel;
	View.Codename = CapitalizeWords(Label);
	View.OrderLine = Item.OrderId.IsEmpty()
		? FString()
		: FString::Printf(TEXT("order #%s"), *Item.OrderId.Left(12));

	const FMantlePlaceVaultStatusView StatusView = GetVaultStatusView(Item.Status);
	View.StatusLabel = StatusView.Label;
	View.StatusColor = StatusView.Color;

	View.MetaLine = FormatMetaLine(Item.AreaKm2, Item.CreatedAt);
	View.ExtentText = FormatExtent(Item.AreaKm2);
	View.TierLabel = UMantlePlaceVaultImportOrchestrator::GetBundleTierLabel(Item);

	// "Delivered as": the dotted format strip (web formatStripLabel). Prefer the per-format download
	// list (what the web maps over), falling back to the coarse `formats` when no artifacts are recorded.
	TArray<FString> DeliveredFormats;
	if (Item.DownloadFormats.Num() > 0)
	{
		DeliveredFormats.Reserve(Item.DownloadFormats.Num());
		for (const FMantlePlaceVaultArtifact& Artifact : Item.DownloadFormats)
		{
			DeliveredFormats.Add(Artifact.Format);
		}
	}
	else
	{
		DeliveredFormats = Item.Formats;
	}
	View.DeliveredAsText = FormatDeliveredAs(DeliveredFormats);

	// Total size: prefer the whole-bundle sidecar size; else sum the per-format artifact sizes; else unknown.
	int64 TotalBytes = 0;
	if (Item.bHasSizeBytes)
	{
		TotalBytes = Item.SizeBytes;
	}
	else
	{
		for (const FMantlePlaceVaultArtifact& Artifact : Item.DownloadFormats)
		{
			TotalBytes += Artifact.ByteSize;
		}
	}
	View.SizeText = FormatVaultBytes(TotalBytes);

	View.bLayersKnown = Item.bLayersKnown;
	View.bImagery = Item.Layers.bImagery;
	View.bBasemap = Item.Layers.bBasemap;
	View.bElevation = Item.Layers.bElevation;
	View.bHasManifestVersion = Item.bHasManifestVersion;
	View.ManifestVersion = Item.ManifestVersion;

	for (const FMantlePlaceVaultArtifact& Artifact : Item.DownloadFormats)
	{
		const FMantlePlaceFormatExplainer Explainer = GetFormatExplainer(Artifact.Format);
		FMantlePlaceVaultArtifactView Row;
		Row.Label = Explainer.Label;
		Row.SizeText = FormatVaultBytes(Artifact.ByteSize);
		Row.OpensWith = Explainer.OpensWith;
		Row.SubLine = Explainer.OpensWith.IsEmpty()
			? Row.SizeText
			: FString::Printf(TEXT("%s %c opens with %s"), *Row.SizeText, (TCHAR)0x00B7 /* middot */, *Explainer.OpensWith);
		View.Artifacts.Add(MoveTemp(Row));
	}

	View.PrimaryLabel = TEXT("Import");
	View.bPrimaryEnabled = (Item.Status == EMantlePlaceVaultBundleStatus::Available) && !bBusy;

	return View;
}
