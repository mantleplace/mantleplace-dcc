// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceSecretStore.h"

#include "MantlePlaceBundleCacheLogic.h" // SanitizeKeySegment / Sha256Hex - the shared HPS-30 mapping

#include "HAL/CriticalSection.h" // FSystemWideCriticalSection: the cross-process store lock
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceSecret, Log, All);

namespace
{
	/**
	 * Resolve the on-disk path for a secret Key, under the per-OS-user Mantle Place auth directory.
	 *
	 * This is deliberately NOT a per-project location. It used to be
	 * FPaths::ProjectSavedDir()/MantlePlace/secret_<key>.bin, which scoped the credential to one
	 * Unreal project directory: a curator signed in again for every project, and lost the session
	 * entirely whenever Saved/ was cleaned. It also put the file somewhere the Revit host could
	 * never look, so the two hosts could not share a session even in principle.
	 *
	 * The key-to-filename mapping matches the Revit host's exactly - the same HPS-30 sanitisation
	 * the bundle cache uses, with the same short digest of the RAW key appended when the mapping
	 * was lossy. Both hosts must derive the same name from the same key, or "one machine identity"
	 * is quietly two.
	 */
	FString ResolveSecretPath(const FString& Key)
	{
		const FString Stem = FMantlePlaceBundleCacheLogic::SanitizeKeySegment(Key);

		FString FileName = Stem;
		if (Stem != Key)
		{
			const FTCHARToUTF8 Utf8(*Key);
			const FString Digest = FMantlePlaceBundleCacheLogic::Sha256Hex(
				reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
			FileName = Stem + TEXT("_") + Digest.Left(8);
		}

		const FString Root = FString(FPlatformProcess::UserSettingsDir());
		if (Root.IsEmpty())
		{
			// No per-user settings location on this platform. Returning an empty path makes Save
			// fail honestly rather than writing the credential somewhere arbitrary.
			return FString();
		}
		return Root / TEXT("MantlePlace") / TEXT("auth") / (FileName + TEXT(".bin"));
	}
}

#if PLATFORM_WINDOWS

#include "Windows/AllowWindowsPlatformTypes.h"
#include <dpapi.h>
#include "Windows/HideWindowsPlatformTypes.h"

/**
 * DPAPI-backed store. CryptProtectData encrypts with a key derived from the logged-in Windows
 * user's credentials (no CRYPTPROTECT_LOCAL_MACHINE), so the blob is unreadable by other users
 * and is correctly bound to the desktop account that signed in.
 */
class FMantlePlaceSecretStoreWindows : public IMantlePlaceSecretStore
{
public:
	/**
	 * Hold the cross-host store lock for the duration of one access.
	 *
	 * A rotation is a read-modify-write on a file two host plugins share, so without this a Revit
	 * refresh and an Unreal refresh can interleave and leave the file holding a token neither
	 * process believes in. The wait is bounded: a lock we cannot take in five seconds is one whose
	 * holder has died or hung, and proceeding unserialised is strictly better than refusing to sign
	 * the user in at all - the failure this guards against is rare, and the failure it would
	 * otherwise cause is total.
	 */
	struct FScopedStoreLock
	{
		FScopedStoreLock()
			: Lock(FString(IMantlePlaceSecretStore::LockName), FTimespan::FromSeconds(5.0))
		{
		}

		FSystemWideCriticalSection Lock;
	};

	virtual bool Save(const FString& Key, const FString& PlaintextValue) override
	{
		FScopedStoreLock StoreLock;

		const FTCHARToUTF8 Utf8(*PlaintextValue);

		DATA_BLOB In;
		In.pbData = reinterpret_cast<BYTE*>(const_cast<ANSICHAR*>(Utf8.Get()));
		In.cbData = static_cast<DWORD>(Utf8.Length());

		DATA_BLOB Out;
		FMemory::Memzero(&Out, sizeof(Out));

		if (!CryptProtectData(&In, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &Out))
		{
			UE_LOG(LogMantlePlaceSecret, Warning, TEXT("CryptProtectData failed; the session will not persist."));
			return false;
		}

		TArray<uint8> Blob;
		Blob.Append(reinterpret_cast<const uint8*>(Out.pbData), static_cast<int32>(Out.cbData));
		LocalFree(Out.pbData);

		const FString Path = ResolveSecretPath(Key);
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), /*Tree=*/true);
		if (!FFileHelper::SaveArrayToFile(Blob, *Path))
		{
			UE_LOG(LogMantlePlaceSecret, Warning, TEXT("Failed to write encrypted secret to '%s'."), *Path);
			return false;
		}
		return true;
	}

	virtual bool Load(const FString& Key, FString& OutPlaintextValue) override
	{
		FScopedStoreLock StoreLock;

		TArray<uint8> Blob;
		if (!FFileHelper::LoadFileToArray(Blob, *ResolveSecretPath(Key)) || Blob.Num() == 0)
		{
			return false;
		}

		DATA_BLOB In;
		In.pbData = reinterpret_cast<BYTE*>(Blob.GetData());
		In.cbData = static_cast<DWORD>(Blob.Num());

		DATA_BLOB Out;
		FMemory::Memzero(&Out, sizeof(Out));

		if (!CryptUnprotectData(&In, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &Out))
		{
			// A blob written by a different Windows user (or a corrupt file) decrypts to nothing —
			// treat as "no stored session" rather than an error.
			UE_LOG(LogMantlePlaceSecret, Verbose, TEXT("CryptUnprotectData failed; treating as no stored session."));
			return false;
		}

		TArray<uint8> Decrypted;
		Decrypted.Append(reinterpret_cast<const uint8*>(Out.pbData), static_cast<int32>(Out.cbData));
		Decrypted.Add(0); // NUL-terminate for UTF-8 interpretation.
		LocalFree(Out.pbData);

		OutPlaintextValue = FString(UTF8_TO_TCHAR(reinterpret_cast<const ANSICHAR*>(Decrypted.GetData())));
		return true;
	}

	virtual void Clear(const FString& Key) override
	{
		FScopedStoreLock StoreLock;

		IFileManager::Get().Delete(*ResolveSecretPath(Key), /*RequireExists=*/false, /*EvenReadOnly=*/true, /*Quiet=*/true);
	}

	virtual bool IsPersistent() const override { return true; }
};

#endif // PLATFORM_WINDOWS

/** Fail-safe fallback for platforms without a secure store: never writes a plaintext secret. */
class FMantlePlaceSecretStoreNull : public IMantlePlaceSecretStore
{
public:
	virtual bool Save(const FString& /*Key*/, const FString& /*PlaintextValue*/) override
	{
		UE_LOG(LogMantlePlaceSecret, Warning,
			TEXT("No secure token store on this platform; the session will not persist across launches."));
		return false;
	}

	virtual bool Load(const FString& /*Key*/, FString& /*OutPlaintextValue*/) override { return false; }
	virtual void Clear(const FString& /*Key*/) override {}
	virtual bool IsPersistent() const override { return false; }
};

TUniquePtr<IMantlePlaceSecretStore> IMantlePlaceSecretStore::Create()
{
#if PLATFORM_WINDOWS
	return MakeUnique<FMantlePlaceSecretStoreWindows>();
#else
	return MakeUnique<FMantlePlaceSecretStoreNull>();
#endif
}
