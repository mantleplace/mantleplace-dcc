// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceDeliveryFacts;

struct FMantlePlaceDeliveryLogic
{
	static TArray<FString> DescribeDelivery(const FMantlePlaceDeliveryFacts& Delivery);
};
