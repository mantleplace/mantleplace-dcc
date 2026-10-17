// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceFoliageVolume.h"

#include "Components/BoxComponent.h"
#include "PCGComponent.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(MantlePlaceFoliageVolume)

AMantlePlaceFoliageVolume::AMantlePlaceFoliageVolume(const FObjectInitializer& ObjectInitializer)
	: Super(ObjectInitializer)
{
	Bounds = CreateDefaultSubobject<UBoxComponent>(TEXT("Bounds"));
	Bounds->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Bounds->SetCollisionProfileName(UCollisionProfile::NoCollision_ProfileName);
	Bounds->SetCanEverAffectNavigation(false);
	Bounds->SetGenerateOverlapEvents(false);
	RootComponent = Bounds;

	Pcg = CreateDefaultSubobject<UPCGComponent>(TEXT("Pcg"));
	// Generated in the editor, at import, and saved with the level: never regenerated at load or at
	// runtime, where the rasters' source data does not exist.
	Pcg->GenerationTrigger = EPCGComponentGenerationTrigger::GenerateOnDemand;
}
