// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.h"
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

FMantlePlaceVaultRowView BuildVaultRowView(const FMantlePlaceVaultItem& Item, bool bBusy);

struct FMantlePlaceVaultStatusView
{
	FString Label;
	FLinearColor Color = FLinearColor::White;
};

FMantlePlaceVaultStatusView GetVaultStatusView(EMantlePlaceVaultBundleStatus Status);

struct FMantlePlaceVaultArtifactView
{
	FString Label;
	FString SizeText;
	FString OpensWith;
	FString SubLine;
};

struct FMantlePlaceFormatExplainer
{
	FString Label;
	FString OpensWith;
};

FMantlePlaceFormatExplainer GetFormatExplainer(const FString& Format);

FString FormatDeliveredAs(const TArray<FString>& Formats);

struct FMantlePlaceVaultExtraView
{
	FString Label;
	FString SubLine;
	bool bLocked = false;
};

struct FMantlePlaceVaultDetailView
{
	FString Codename;
	FString OrderLine;
	FString StatusLabel;
	FLinearColor StatusColor = FLinearColor::White;
	FString MetaLine;
	FString TierLabel;
	FString ExtentText;
	FString DeliveredAsText;
	FString SizeText;
	bool bLayersKnown = false;
	bool bImagery = false;
	bool bBasemap = false;
	bool bElevation = false;
	bool bHasManifestVersion = false;
	FString ManifestVersion;
	TArray<FMantlePlaceVaultArtifactView> Artifacts;
	TArray<FMantlePlaceVaultExtraView> Extras;
	FString PaidOnlyNote;
	FString PrimaryLabel;
	bool bPrimaryEnabled = false;
};

FMantlePlaceVaultDetailView BuildVaultDetailView(const FMantlePlaceVaultItem& Item, bool bBusy);

FString FormatVaultBytes(int64 Bytes);
