// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#if WITH_DEV_AUTOMATION_TESTS

#include "HAL/FileManager.h"
#include "Misc/PackageName.h"
#include "Misc/PackagePath.h"
#include "Misc/Paths.h"

/**
 * Where a test saves a level and its packages: under the plugin's own /MantlePlace/ root, so the
 * plugin's asset domain covers them in a project that enables AssetReferenceRestrictions (a mount
 * root of the test's own matches no domain, and the domain lookup ensures), yet on disk in the
 * project's Intermediate, never in the plugin's Content or the project's.
 *
 * Three nested mount points, because World Partition files a level's actors under the first path
 * segment, not the level's own mount (ULevel::GetExternalActorsPath): a level at
 * /MantlePlace/<Name>/<Map> keeps its actors in /MantlePlace/__ExternalActors__/<Name>/<Map>/ and
 * their objects in /MantlePlace/__ExternalObjects__/<Name>/<Map>/. The deepest mount resolves a
 * package, so each of the three lands in its own folder under Intermediate/<Name>/.
 *
 * Not /Temp/: the asset registry will not scan it, and World Partition reads no actor descriptor
 * from a /Temp level, so a level saved there cannot be read back.
 */
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

	/** Clear what an earlier run left on disk, then register the mount points. */
	void Mount()
	{
		IFileManager::Get().DeleteDirectory(*DiskRoot, /*RequireExists*/ false, /*Tree*/ true);
		for (const TPair<FString, FString>& Pair : Roots)
		{
			IFileManager::Get().MakeDirectory(*Pair.Value, /*Tree*/ true);
			FPackageName::RegisterMountPoint(Pair.Key, Pair.Value);
		}
		bMounted = true;
	}

	/** Remove the mount points and everything saved under them. Safe to call twice. */
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
		IFileManager::Get().DeleteDirectory(*DiskRoot, /*RequireExists*/ false, /*Tree*/ true);
	}

	/** True for a package under any of the mount points: the level, its actors, their objects. */
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
	/** The level's package root, /MantlePlace/<Name>/. */
	const FString Root;
	/** Intermediate/<Name>/, holding every file saved under the mount points. */
	const FString DiskRoot;
	/** Each mount point's package root and the folder it maps to. */
	TArray<TPair<FString, FString>> Roots;

private:
	bool bMounted = false;
};

#endif // WITH_DEV_AUTOMATION_TESTS
