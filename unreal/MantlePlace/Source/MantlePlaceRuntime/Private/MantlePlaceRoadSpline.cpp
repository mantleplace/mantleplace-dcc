// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceRoadSpline.h"

#include "Components/SplineComponent.h"

AMantlePlaceRoadSpline::AMantlePlaceRoadSpline()
{
	PrimaryActorTick.bCanEverTick = false;

	Spline = CreateDefaultSubobject<USplineComponent>(TEXT("RoadSpline"));
	SetRootComponent(Spline);
}
