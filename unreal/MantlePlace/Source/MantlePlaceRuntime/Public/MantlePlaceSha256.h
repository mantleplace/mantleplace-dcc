// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct MANTLEPLACERUNTIME_API FMantlePlaceSha256
{
	FMantlePlaceSha256();

	void Update(const uint8* Data, int64 NumBytes);

	void FinalBytes(uint8 (&OutHash)[32]);

	FString Final();

	static FString HexDigest(TConstArrayView<uint8> Bytes);

private:
	uint32 H[8];
	uint8 Pending[64];
	int32 PendingLen = 0;
	uint64 TotalBytes = 0;
};
