// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"

#include "MantlePlaceAutomationLibrary.generated.h"

class AActor;
class UWorld;

/** Where Open in curator's extent came from. Mirrors EMantlePlaceExtentSource, value for value. */
UENUM(BlueprintType)
enum class EMantlePlaceCuratorSource : uint8
{
	/** The level holds no source: the plain curator opens. */
	None,
	/** A previous Mantle Place import's provenance record. */
	PriorImport,
	/** The level's native georeferencing (Epic's GeoReferencing system actor). */
	NativeGeoreference,
	/** A Cesium georeference actor. */
	CesiumGeoreference,
	/** A Mantle Place Extent box. */
	ExtentBox,
};

/** What the Open in curator button would act on, read without opening anything. */
USTRUCT(BlueprintType)
struct FMantlePlaceCuratorResolution
{
	GENERATED_BODY()

	/** The source that supplied the extent; None when the level has none. */
	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	EMantlePlaceCuratorSource Source = EMantlePlaceCuratorSource::None;

	/** The source in words, as the button's toast says it ("the Extent box"). */
	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	FString SourceDescription;

	/** True when Source is not None, i.e. West/South/East/North hold an extent. */
	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	bool bHasExtent = false;

	/** The extent in WGS84 degrees; all zero when bHasExtent is false. */
	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	double West = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	double South = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	double East = 0.0;

	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	double North = 0.0;

	/**
	 * The link the button opens. With no source it is the plain curator link, the one the no-extent
	 * prompt's "Open the curator" opens (the button itself shows that prompt and opens nothing). Empty
	 * when the configured web-app host is not http(s), which means "open nothing".
	 */
	UPROPERTY(BlueprintReadOnly, Category = "MantlePlace|Automation")
	FString Url;
};

/**
 * Editor-scripting access to the plugin's tagged Slate widgets.
 *
 * The vault panel names its interactive controls with FTagMetaData
 * ("MantlePlace.Vault.SignIn", "MantlePlace.Vault.Import", ...). This library
 * resolves a tag to the widget's CURRENT on-screen rectangle so external
 * tooling — UI automation, accessibility layers, scripted walkthroughs — can
 * target controls semantically instead of by remembered pixel positions,
 * which rot whenever the layout shifts (the detail page's Import button moves
 * with the conditional MANIFEST row above it, for example).
 *
 * Reachability is part of the contract: a tag on a widget that exists but is
 * not in the currently-arranged layout (the vault panel is an SWidgetSwitcher
 * of two pages, so half its controls are always dormant) resolves as NOT
 * found rather than as a stale rectangle. Stale-but-plausible is the failure
 * mode that costs the most downstream, so the lookup validates a live widget
 * path instead of trusting cached paint geometry.
 */
UCLASS()
class MANTLEPLACEEDITOR_API UMantlePlaceAutomationLibrary : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Screen-space rectangle of the first REACHABLE widget tagged `Tag`.
	 *
	 * OutCenter/OutSize are in desktop (physical screen) coordinates, the
	 * space OS-level pointers act in. Returns false when no reachable widget
	 * carries the tag — including when the widget exists on a dormant page.
	 */
	UFUNCTION(BlueprintCallable, Category = "MantlePlace|Automation")
	static bool GetTaggedWidgetScreenRect(FName Tag, FVector2D& OutCenter, FVector2D& OutSize);

	/**
	 * Every "MantlePlace."-prefixed widget tag currently REACHABLE on screen.
	 * A diagnostic complement to GetTaggedWidgetScreenRect: what could be
	 * resolved right now, on the page the UI is actually showing.
	 */
	UFUNCTION(BlueprintCallable, Category = "MantlePlace|Automation")
	static TArray<FName> ListReachableWidgetTags();

	/**
	 * The visible text of the first REACHABLE widget tagged `Tag` — the tagged
	 * widget's first descendant text block. Lets tooling read STATE the label
	 * carries (the auth button is a toggle whose text is the only outward truth
	 * of the panel's session) instead of inferring it from geometry or from
	 * files whose lifecycle differs from the widget's.
	 */
	UFUNCTION(BlueprintCallable, Category = "MantlePlace|Automation")
	static bool GetTaggedWidgetText(FName Tag, FString& OutText);

	/**
	 * Open in curator, without the click and without a browser: resolve the current editor world's
	 * extent exactly as the panel's button does (MantlePlaceLevelExtent::Resolve, the same sources in
	 * the same order) and return the source, the W,S,E,N extent and the link the button would open
	 * (MantlePlaceLevelExtent::CuratorUrl). Opens nothing and changes nothing.
	 */
	UFUNCTION(BlueprintCallable, Category = "MantlePlace|Automation")
	static FMantlePlaceCuratorResolution ResolveCuratorExtent();

	/** ResolveCuratorExtent over any world. Not a UFUNCTION: the automation test hands it a throwaway world. */
	static FMantlePlaceCuratorResolution ResolveCuratorExtentInWorld(UWorld* World);

	/**
	 * Place an Extent box in the current editor level, as the no-extent prompt's "Place Extent box"
	 * does (MantlePlaceLevelExtent::PlaceExtentBox: one undo transaction, at the middle of the level's
	 * bounds or the origin, labelled and selected). A level that already has one gets that one selected
	 * and returned instead of a second. Returns the box, or null without an editor world.
	 */
	UFUNCTION(BlueprintCallable, Category = "MantlePlace|Automation")
	static AActor* PlaceExtentBox();

	/** PlaceExtentBox over any world. Not a UFUNCTION. */
	static AActor* PlaceExtentBoxInWorld(UWorld* World);
};
