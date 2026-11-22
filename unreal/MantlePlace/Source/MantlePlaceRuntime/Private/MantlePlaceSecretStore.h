// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class IMantlePlaceSecretStore
{
public:
	static constexpr const TCHAR* LockName = TEXT("MantlePlace.Auth.SecretStore");

	virtual ~IMantlePlaceSecretStore() = default;

	virtual bool Save(const FString& Key, const FString& PlaintextValue) = 0;

	virtual bool Load(const FString& Key, FString& OutPlaintextValue) = 0;

	virtual void Clear(const FString& Key) = 0;

	virtual bool IsPersistent() const = 0;

	static TUniquePtr<IMantlePlaceSecretStore> Create();
};
