// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceSecretStore.h"

#include "MantlePlaceBundleCacheLogic.h"

#include "HAL/CriticalSection.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogMantlePlaceSecret, Log, All);

namespace
{
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
			return FString();
		}
		return Root / TEXT("MantlePlace") / TEXT("auth") / (FileName + TEXT(".bin"));
	}
}

#if PLATFORM_WINDOWS

#include "Windows/AllowWindowsPlatformTypes.h"
#include <dpapi.h>
#include "Windows/HideWindowsPlatformTypes.h"

class FMantlePlaceSecretStoreWindows : public IMantlePlaceSecretStore
{
public:
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
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
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
			UE_LOG(LogMantlePlaceSecret, Verbose, TEXT("CryptUnprotectData failed; treating as no stored session."));
			return false;
		}

		TArray<uint8> Decrypted;
		Decrypted.Append(reinterpret_cast<const uint8*>(Out.pbData), static_cast<int32>(Out.cbData));
		Decrypted.Add(0);
		LocalFree(Out.pbData);

		OutPlaintextValue = FString(UTF8_TO_TCHAR(reinterpret_cast<const ANSICHAR*>(Decrypted.GetData())));
		return true;
	}

	virtual void Clear(const FString& Key) override
	{
		FScopedStoreLock StoreLock;

		IFileManager::Get().Delete(*ResolveSecretPath(Key), false, true, true);
	}

	virtual bool IsPersistent() const override { return true; }
};

#endif

class FMantlePlaceSecretStoreNull : public IMantlePlaceSecretStore
{
public:
	virtual bool Save(const FString&, const FString&) override
	{
		UE_LOG(LogMantlePlaceSecret, Warning,
			TEXT("No secure token store on this platform; the session will not persist across launches."));
		return false;
	}

	virtual bool Load(const FString&, FString&) override { return false; }
	virtual void Clear(const FString&) override {}
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
