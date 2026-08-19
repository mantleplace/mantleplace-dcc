// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * SHA-256 over a byte buffer, returned as a 64-char lowercase hex digest — used by the importer to
 * verify a bundle's extracted heightmap/imagery bytes against the manifest's declared sha256
 * (fail-closed integrity check; see MantlePlaceImporterLibrary).
 *
 * NOTE: self-contained (FIPS 180-4) because FPlatformMisc::GetSHA256Signature asserts
 * "No SHA256 Platform implementation" on Windows in UE 5.8. This duplicates the proven SHA-256 in
 * the Runtime auth layer (MantlePlaceAuthLogic.cpp) to avoid an Editor->Runtime dependency on the
 * shipped auth system; consolidate into a shared util module if one ever exists.
 */
namespace MantlePlaceSha256
{
	/** Lowercase 64-char hex SHA-256 digest of the given bytes. */
	FString HexDigest(TConstArrayView<uint8> Bytes);
}
