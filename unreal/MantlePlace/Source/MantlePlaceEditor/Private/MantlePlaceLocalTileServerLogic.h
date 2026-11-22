// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceLocalTileServerLogic
{
	static FString ResolveUnderRoot(const FString& RootDirAbs, const FString& RequestPath);

	static FString ContentTypeFor(const FString& FilePath, bool& bOutGzip);
};
