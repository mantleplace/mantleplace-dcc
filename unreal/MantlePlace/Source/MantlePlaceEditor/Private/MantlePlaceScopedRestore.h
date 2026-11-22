// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"

class FMantlePlaceScopedRestore
{
public:
	explicit FMantlePlaceScopedRestore(TFunction<void()> InRestore)
		: Restore(MoveTemp(InRestore))
	{
	}

	~FMantlePlaceScopedRestore()
	{
		if (Restore)
		{
			Restore();
			Restore.Reset();
		}
	}

	FMantlePlaceScopedRestore(const FMantlePlaceScopedRestore&) = delete;
	FMantlePlaceScopedRestore& operator=(const FMantlePlaceScopedRestore&) = delete;
	FMantlePlaceScopedRestore(FMantlePlaceScopedRestore&&) = delete;
	FMantlePlaceScopedRestore& operator=(FMantlePlaceScopedRestore&&) = delete;

	void Disarm()
	{
		Restore.Reset();
	}

	bool IsArmed() const
	{
		return static_cast<bool>(Restore);
	}

private:
	TFunction<void()> Restore;
};
