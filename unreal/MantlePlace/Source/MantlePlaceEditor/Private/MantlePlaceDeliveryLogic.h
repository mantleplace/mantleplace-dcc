// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceDeliveryFacts;

/**
 * Pure logic for what the import summary says about a bundle's `delivery` block: the unit system, the
 * linear unit and the delivery CRS, in the words HPS-51 fixes for every host, and -- because Unreal is
 * a fixed-frame host (HPS-54) -- that this host's content is metric when the delivered files are not.
 *
 * A user who opens the bundle's GIS or CAD files beside their level on an imperial order meets feet
 * with nothing on screen having said so. The second line is that warning, and it describes the bundle
 * rather than apologising for it.
 *
 * Display only. Nothing here, and nothing that reads these lines, places anything.
 */
struct FMantlePlaceDeliveryLogic
{
	/**
	 * The lines to show, in order:
	 *   - none, when the manifest carried no `delivery` block -- a bundle from before the block
	 *     existed, which is not assumed to be metric;
	 *   - "Bundle delivery: <unit system> · <linear unit> · <delivery CRS>", whenever it did;
	 *   - the fixed-frame line, only when the linear unit is a foot (`ftUS`, `ft`). It is keyed on the
	 *     files' unit rather than the unit system, which is the customer's choice and does not by itself
	 *     say what a file is in; a unit this host has no word for is shown as published and earns no
	 *     second line, because saying its files are in feet would be a guess.
	 */
	static TArray<FString> DescribeDelivery(const FMantlePlaceDeliveryFacts& Delivery);
};
