// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"

#include "MantlePlaceCuratorExtentLogic.h"

#include "MantlePlaceExtentBox.generated.h"

class UBoxComponent;

/**
 * The Extent box: a box the user places and scales in the level to say "this is my site", for a
 * level that has no previous import, no georeference and no Cesium georeference to say it for them.
 * Open in curator reads it and opens the web curator on its extent.
 *
 * What a level cannot say by itself is where on Earth the box is, so the box says it: set
 * Centre Longitude and Centre Latitude (WGS84 degrees) to the site's centre, then scale the box over
 * the site. Its extent is W,S,E,N degrees: the centre, plus and minus half the box's scaled size
 * (X is east-west, Y is north-south) converted to degrees. Rotation is ignored, because the curator
 * draws an axis-aligned extent. The box is a marker for the curator handoff and nothing more: it
 * reads, and moves, nothing.
 *
 * In the Runtime module so the class loads wherever the level does, but an editor-only actor: it
 * is stripped from a cooked build, where it has no meaning.
 */
UCLASS(NotBlueprintable, meta = (DisplayName = "Mantle Place Extent Box"))
class MANTLEPLACERUNTIME_API AMantlePlaceExtentBox : public AActor
{
	GENERATED_BODY()

public:
	AMantlePlaceExtentBox(const FObjectInitializer& ObjectInitializer = FObjectInitializer::Get());

	/** The box the user scales over the site. Its scaled X and Y extents are the extent's width and depth. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place")
	TObjectPtr<UBoxComponent> Bounds;

	/** Longitude of the box's centre, WGS84 degrees east. Exactly 0 with latitude 0 means "not located yet". */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Mantle Place", meta = (ClampMin = "-180", ClampMax = "180", Units = "deg"))
	double CenterLongitude = 0.0;

	/** Latitude of the box's centre, WGS84 degrees north. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Mantle Place", meta = (ClampMin = "-90", ClampMax = "90", Units = "deg"))
	double CenterLatitude = 0.0;

	/** The default half-size of the box in Unreal units (cm): 500 m a side, so a 1 km box before any scaling. */
	static constexpr float DefaultHalfSizeCm = 50000.f;

	/** What Open in curator needs from the box: its centre in degrees and its size in metres. */
	FMantlePlaceExtentBoxState GetState() const;

	/** The box's extent in W,S,E,N degrees. False while the box is not located or has no size. */
	bool GetExtent(FMantlePlaceExtent& OutExtent) const;
};
