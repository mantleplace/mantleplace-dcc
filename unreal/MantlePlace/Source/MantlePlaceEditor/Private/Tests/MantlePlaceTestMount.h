// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#if WITH_DEV_AUTOMATION_TESTS

#include "HAL/FileManager.h"
#include "Misc/PackageName.h"
#include "Misc/PackagePath.h"
#include "Misc/Paths.h"

struct FMantlePlaceTestMount
{
	explicit FMantlePlaceTestMount(const TCHAR* InName)
	    : Name(InName), Root(FString::Printf(TEXT("/MantlePlace/%s/"), InName)), DiskRoot(FPaths::ConvertRelativePathToFull(FPaths::ProjectIntermediateDir() / InName / TEXT("")))
	{
		Roots = {
			{ Root, DiskRoot / TEXT("Level/") },
			{ FString::Printf(TEXT("/MantlePlace/%s/%s/"), FPackagePath::GetExternalActorsFolderName(), InName), DiskRoot / TEXT("ExternalActors/") },
			{ FString::Printf(TEXT("/MantlePlace/%s/%s/"), FPackagePath::GetExternalObjectsFolderName(), InName), DiskRoot / TEXT("ExternalObjects/") },
		};
	}

	~FMantlePlaceTestMount()
	{
		Unmount();
	}

	void Mount()
	{
		IFileManager::Get().DeleteDirectory(*DiskRoot, false, true);
		for (const TPair<FString, FString>& Pair : Roots)
		{
			IFileManager::Get().MakeDirectory(*Pair.Value, true);
			FPackageName::RegisterMountPoint(Pair.Key, Pair.Value);
		}
		bMounted = true;
	}

	void Unmount()
	{
		if (bMounted)
		{
			for (const TPair<FString, FString>& Pair : Roots)
			{
				FPackageName::UnRegisterMountPoint(Pair.Key, Pair.Value);
			}
			bMounted = false;
		}
		IFileManager::Get().DeleteDirectory(*DiskRoot, false, true);
	}

	bool Owns(const FString& PackageName) const
	{
		for (const TPair<FString, FString>& Pair : Roots)
		{
			if (PackageName.StartsWith(Pair.Key))
			{
				return true;
			}
		}
		return false;
	}

	const FString Name;
	const FString Root;
	const FString DiskRoot;
	TArray<TPair<FString, FString>> Roots;

private:
	bool bMounted = false;
};

#endif
