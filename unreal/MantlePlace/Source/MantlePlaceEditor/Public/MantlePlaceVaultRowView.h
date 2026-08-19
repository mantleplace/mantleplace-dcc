// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.h" // FMantlePlaceVaultItem + EMantlePlaceVaultBundleStatus
#include "MantlePlaceVaultRowView.generated.h"

/**
 * Fully-computed presentation state for one vault-list row. All of the row's display logic
 * (status -> label/tint, tier -> primary-button label, area/date formatting, enabled/reason)
 * is derived once in BuildVaultRowView so the row WidgetBlueprint stays surface-only: it just
 * paints these fields. Kept in C++ so the logic is reviewable in a diff and unit-tested headless.
 */
USTRUCT(BlueprintType)
struct FMantlePlaceVaultRowView
{
	GENERATED_BODY()

	/** AOI codename/label, capitalized (heading text). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString Codename;

	/** Monospace sub-label "{area} km2 - {date}" (e.g. "12.34 km2 - Jun 17, 2026"). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString MetaLine;

	/** Status pill text without the bullet, e.g. "ready" | "processing" | "refunded" | "failed" | "unknown". */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString StatusLabel;

	/** Status accent (pill text at full strength; pill fill uses this at ~15% alpha). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FLinearColor StatusColor = FLinearColor::White;

	/** Tier badge: "Base" (needs Generate) | "Unreal" (ready) | "Unknown". */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString TierLabel;

	/** Primary action label, e.g. "GENERATE + IMPORT" | "IMPORT" | "TRY AGAIN". */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString PrimaryLabel;

	/** Whether the primary action is clickable (false while any import is running, or when not actionable). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bPrimaryEnabled = false;

	/** Whether the tier-2 reason line is shown (disabled/non-actionable states). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	bool bShowReason = false;

	/** Reason line text, e.g. "generating... check back" (only meaningful when bShowReason). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FString ReasonText;

	/** Reason line tint (matches StatusColor). */
	UPROPERTY(BlueprintReadOnly, Category = "Mantle Place|Vault")
	FLinearColor ReasonColor = FLinearColor::White;
};

/**
 * Pure presentation seam: derive a row's display state from a vault item + the orchestrator's busy
 * flag. No UObject / orchestrator instance needed, so it is trivially unit-testable. bBusy true means
 * an import is already running (only one runs at a time) -> every row's primary action is disabled.
 */
FMantlePlaceVaultRowView BuildVaultRowView(const FMantlePlaceVaultItem& Item, bool bBusy);

/** Status pill presentation (label + accent) shared by the row and the details page. */
struct FMantlePlaceVaultStatusView
{
	FString Label;                            // "ready" | "processing" | "refunded" | "failed" | "unknown"
	FLinearColor Color = FLinearColor::White; // accent at full strength; pill fill uses it at ~15% alpha
};

/** Map a bundle status to its pill label + web-brand accent. */
FMantlePlaceVaultStatusView GetVaultStatusView(EMantlePlaceVaultBundleStatus Status);

/** One artifact row on the details page (an info-only per-format entry; the editor imports, not downloads). */
struct FMantlePlaceVaultArtifactView
{
	FString Label;     // dotted format label, e.g. ".glb" (web format-explainers.ts)
	FString SizeText;  // human size (e.g. "12.3 MB") or "-" when unrecorded
	FString OpensWith; // "opens with" tool list (e.g. "Blender, Unreal Engine, three.js"); empty if unknown format
	FString SubLine;   // precomputed mono sub-line: "{size} · opens with {tools}" (or just "{size}")
};

/** A bundle artifact format's display label + "opens with" copy (mirror of web format-explainers.ts). */
struct FMantlePlaceFormatExplainer
{
	FString Label;     // dotted, e.g. ".glb"; falls back to "." + lowercased format for unknowns
	FString OpensWith; // comma-separated tool list; empty for unknown formats
};

/** Look up the label + "opens with" copy for a bundle artifact format (case-insensitive). */
FMantlePlaceFormatExplainer GetFormatExplainer(const FString& Format);

/** Compact "·"-joined dotted label strip for a set of formats (web formatStripLabel), e.g.
 *  ".glb · .geotiff · .pmtiles"; returns "-" when the set is empty. */
FString FormatDeliveredAs(const TArray<FString>& Formats);

/**
 * Fully-computed presentation state for the bundle details sub-page. Like the row view it is a pure
 * derivation from the item (+ busy flag) so the detail widget stays a dumb painter and the logic is
 * unit-testable. Deliberately omits any advanced-processing / format-catalog fields - the editor
 * surface only imports; format generation lives on the website.
 */
struct FMantlePlaceVaultDetailView
{
	FString Codename;                          // capitalized AOI label
	FString OrderLine;                         // "order #<first 12 of id>"
	FString StatusLabel;
	FLinearColor StatusColor = FLinearColor::White;
	FString MetaLine;                          // "{area} km2 - {date}"
	FString TierLabel;                         // "Base" | "Unreal" | "Unknown"
	FString ExtentText;                        // "{area} km2"
	FString DeliveredAsText;                   // "·"-joined dotted formats (".glb · .cog"), or "-" when none
	FString SizeText;                          // total download size, or "-" when unknown
	bool bLayersKnown = false;
	bool bImagery = false;
	bool bBasemap = false;
	bool bElevation = false;
	bool bHasManifestVersion = false;
	int32 ManifestVersion = 0;
	TArray<FMantlePlaceVaultArtifactView> Artifacts;
	FString PrimaryLabel;                      // "Import"
	bool bPrimaryEnabled = false;              // Available && !bBusy
};

/** Derive the details-page presentation for a vault item (bBusy disables the import action). */
FMantlePlaceVaultDetailView BuildVaultDetailView(const FMantlePlaceVaultItem& Item, bool bBusy);

/** Format a byte count as a short human string ("512 KB", "12.3 MB"); returns "-" for a non-positive size. */
FString FormatVaultBytes(int64 Bytes);
