// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceLocalTileServerLogic.h"

#include "Misc/Paths.h"

namespace
{
	// The one encoding a client must be told about: CTB writes quantized-mesh tiles gzipped on disk.
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

	// e.g. "/Terrain/14/5615/11520.terrain" -> <root>/Terrain/14/5615/11520.terrain.
	// FHttpPath normalizes to a single leading slash, so one RemoveFromStart suffices; the loop is
	// not needed and a doubled slash would be caught by the containment check below in any case.
	FString Rel = RequestPath;
	Rel.RemoveFromStart(TEXT("/"));
	if (Rel.IsEmpty() || Rel.Contains(TEXT("..")))
	{
		return FString();
	}

	FString Candidate = FPaths::ConvertRelativePathToFull(FPaths::Combine(RootDirAbs, Rel));
	FPaths::NormalizeFilename(Candidate);

	// Containment guard: the resolved path must stay strictly under the root. `+ "/"` is what makes
	// it strict — without it a sibling directory whose name merely starts with the root's would pass,
	// and so would the root itself, which is a directory and not a file to serve.
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
	const FString Ext = FPaths::GetExtension(FilePath, /*bIncludeDot*/ false).ToLower();
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
