// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceSecretStore.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceSecret, Log, All);

namespace
{
	/** Resolve the on-disk path for a secret Key, under the ignored Saved/MantlePlace directory. */
	FString ResolveSecretPath(const FString& Key)
	{
		FString Safe;
		Safe.Reserve(Key.Len());
		for (const TCHAR Ch : Key)
		{
			Safe.AppendChar(FChar::IsAlnum(Ch) ? Ch : TEXT('_'));
		}
		return FPaths::ProjectSavedDir() / TEXT("MantlePlace") / FString::Printf(TEXT("secret_%s.bin"), *Safe);
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
	virtual bool Save(const FString& Key, const FString& PlaintextValue) override
	{
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
