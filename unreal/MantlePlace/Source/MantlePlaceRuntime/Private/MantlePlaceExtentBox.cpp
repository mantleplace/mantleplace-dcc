// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceExtentBox.h"

#include "Components/BoxComponent.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(MantlePlaceExtentBox)

AMantlePlaceExtentBox::AMantlePlaceExtentBox(const FObjectInitializer& ObjectInitializer)
	: Super(ObjectInitializer)
{
	// A marker for the curator handoff: nothing about it belongs in a cooked build.
	bIsEditorOnlyActor = true;

	Bounds = CreateDefaultSubobject<UBoxComponent>(TEXT("Bounds"));
	Bounds->SetBoxExtent(FVector(DefaultHalfSizeCm, DefaultHalfSizeCm, DefaultHalfSizeCm * 0.1f));
	Bounds->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Bounds->SetCollisionProfileName(UCollisionProfile::NoCollision_ProfileName);
	Bounds->SetCanEverAffectNavigation(false);
	Bounds->SetGenerateOverlapEvents(false);
	Bounds->SetHiddenInGame(true);
	Bounds->ShapeColor = FColor(255, 140, 0);
	Bounds->bDrawOnlyIfSelected = false;
	RootComponent = Bounds;
}

FMantlePlaceExtentBoxState AMantlePlaceExtentBox::GetState() const
{
	FMantlePlaceExtentBoxState State;
	State.CenterLongitude = CenterLongitude;
	State.CenterLatitude = CenterLatitude;
	if (Bounds != nullptr)
	{
		// Scaled by the component's world transform, so scaling the actor in the level is what sizes it.
		// Unreal units are centimetres; the box's extent is a half-size.
		const FVector Half = Bounds->GetScaledBoxExtent();
		State.WidthM = 2.0 * Half.X / 100.0;
		State.DepthM = 2.0 * Half.Y / 100.0;
	}
	return State;
}

bool AMantlePlaceExtentBox::GetExtent(FMantlePlaceExtent& OutExtent) const
{
	return FMantlePlaceCuratorExtentLogic::FromExtentBox(GetState(), OutExtent);
}
