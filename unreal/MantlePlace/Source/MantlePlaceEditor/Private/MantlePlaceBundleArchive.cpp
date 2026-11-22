// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceBundleArchive.h"

#include "FileUtilities/ZipArchiveReader.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

FMantlePlaceBundleRead MantlePlaceBundleArchive::Read(const FString& Path)
{
	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();

	if (Platform.DirectoryExists(*Path))
	{
		FString Root = FPaths::ConvertRelativePathToFull(Path);
		FPaths::NormalizeDirectoryName(Root);
		TArray<FString> Found;
		IFileManager::Get().FindFilesRecursive(Found, *Root, TEXT("*"), true, false);
		TArray<FString> Names;
		Names.Reserve(Found.Num());
		for (FString File : Found)
		{
			FPaths::NormalizeFilename(File);
			if (File.StartsWith(Root + TEXT("/")))
			{
				Names.Add(File.Mid(Root.Len() + 1));
			}
		}
		return FMantlePlaceBundleReaderLogic::Read(Names, [&Root](const FString& Name, FString& OutText)
		{
			return FFileHelper::LoadFileToString(OutText, *FPaths::Combine(Root, Name));
		});
	}

	FMantlePlaceBundleRead Failed;
	IFileHandle* Handle = Platform.FileExists(*Path) ? Platform.OpenRead(*Path) : nullptr;
	if (Handle == nullptr)
	{
		Failed.Error = FString::Printf(TEXT("Bundle not found: %s"), *Path);
		return Failed;
	}
	const FZipArchiveReader Zip(Handle);
	if (!Zip.IsValid())
	{
		Failed.Error = FString::Printf(TEXT("Not a folder or a readable zip archive: %s"), *Path);
		return Failed;
	}
	return FMantlePlaceBundleReaderLogic::Read(Zip.GetFileNames(), [&Zip](const FString& Name, FString& OutText)
	{
		TArray<uint8> Bytes;
		if (!Zip.TryReadFile(Name, Bytes))
		{
			return false;
		}
		FFileHelper::BufferToString(OutText, Bytes.GetData(), Bytes.Num());
		return true;
	});
}
