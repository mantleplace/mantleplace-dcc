// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceRoadSpline.h"

#include "Components/SplineComponent.h"

AMantlePlaceRoadSpline::AMantlePlaceRoadSpline()
{
	PrimaryActorTick.bCanEverTick = false;

	Spline = CreateDefaultSubobject<USplineComponent>(TEXT("RoadSpline"));
	SetRootComponent(Spline);
}
