// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"

#include "MantlePlaceFoliageVolume.generated.h"

class UBoxComponent;
class UPCGComponent;

/**
 * The actor a foliage scatter generates from: a box over the AOI and the PCG component that runs the
 * foliage graph inside it. The box gives the component its bounds, which is how a partitioned PCG
 * component knows which World Partition cells it covers; it has no collision, so it neither blocks
 * a trace nor is hit by the scatter's own ground trace.
 *
 * In the Runtime module because it is saved in the level and must load in a cooked build, where its
 * generated instances are what ships.
 */
UCLASS(NotBlueprintable, meta = (DisplayName = "Mantle Place Foliage Volume"))
class MANTLEPLACERUNTIME_API AMantlePlaceFoliageVolume : public AActor
{
	GENERATED_BODY()

public:
	AMantlePlaceFoliageVolume(const FObjectInitializer& ObjectInitializer = FObjectInitializer::Get());

	/** The AOI box; its extent is what the PCG component generates within. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place")
	TObjectPtr<UBoxComponent> Bounds;

	/** The PCG component running the foliage graph. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place")
	TObjectPtr<UPCGComponent> Pcg;
};
