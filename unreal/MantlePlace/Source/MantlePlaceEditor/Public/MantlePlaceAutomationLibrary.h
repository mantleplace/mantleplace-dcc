// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"

#include "MantlePlaceAutomationLibrary.generated.h"

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
};
