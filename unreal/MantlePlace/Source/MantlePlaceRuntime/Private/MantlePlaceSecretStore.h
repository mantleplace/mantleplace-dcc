// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * Platform-abstracted at-rest store for a single auth secret (the OAuth refresh token).
 *
 * The Windows implementation encrypts the value with DPAPI (CryptProtectData) bound to the
 * current Windows user, then writes the ciphertext under the project's Saved/ directory.
 * Platforms without a secure-store implementation get a fail-safe no-op: nothing is written
 * (a plaintext secret on disk is never an option), so the user simply re-authenticates each
 * launch. The access token (short-lived JWT) is intentionally NOT stored — it stays in memory
 * and is re-minted from the refresh token.
 */
class IMantlePlaceSecretStore
{
public:
	virtual ~IMantlePlaceSecretStore() = default;

	/** Encrypt and persist PlaintextValue under Key. Returns false if storage is unavailable. */
	virtual bool Save(const FString& Key, const FString& PlaintextValue) = 0;

	/** Load and decrypt the value stored under Key. Returns false if absent or unreadable. */
	virtual bool Load(const FString& Key, FString& OutPlaintextValue) = 0;

	/** Remove any stored value under Key. Safe to call when nothing is stored. */
	virtual void Clear(const FString& Key) = 0;

	/** True if this store actually persists secrets (false for the no-op fallback). */
	virtual bool IsPersistent() const = 0;

	/** Construct the platform-appropriate secret store. */
	static TUniquePtr<IMantlePlaceSecretStore> Create();
};
