// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLocalTileServerLogic.h"

#include "Misc/Paths.h"

namespace
{
	const TCHAR* const QuantizedMeshContentType =
		TEXT("application/vnd.quantized-mesh;extensions=octvertexnormals");
}

FString FMantlePlaceLocalTileServerLogic::ResolveUnderRoot(
	const FString& RootDirAbs, const FString& RequestPath)
{
	if (RootDirAbs.IsEmpty())
	{
		return FString();
	}

	FString Rel = RequestPath;
	Rel.RemoveFromStart(TEXT("/"));
	if (Rel.IsEmpty() || Rel.Contains(TEXT("..")))
	{
		return FString();
	}

	FString Candidate = FPaths::ConvertRelativePathToFull(FPaths::Combine(RootDirAbs, Rel));
	FPaths::NormalizeFilename(Candidate);

	FString RootWithSlash = RootDirAbs;
	FPaths::NormalizeFilename(RootWithSlash);
	if (!Candidate.StartsWith(RootWithSlash + TEXT("/")))
	{
		return FString();
	}
	return Candidate;
}

FString FMantlePlaceLocalTileServerLogic::ContentTypeFor(const FString& FilePath, bool& bOutGzip)
{
	bOutGzip = false;
	const FString Ext = FPaths::GetExtension(FilePath, false).ToLower();
	if (Ext == TEXT("terrain"))
	{
		bOutGzip = true;
		return QuantizedMeshContentType;
	}
	if (Ext == TEXT("json")) { return TEXT("application/json"); }
	if (Ext == TEXT("png")) { return TEXT("image/png"); }
	if (Ext == TEXT("jpg") || Ext == TEXT("jpeg")) { return TEXT("image/jpeg"); }
	return TEXT("application/octet-stream");
}
