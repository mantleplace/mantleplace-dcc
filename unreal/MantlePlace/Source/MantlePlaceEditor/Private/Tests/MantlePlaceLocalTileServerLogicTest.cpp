// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceLocalTileServerLogic.h"

#include "Misc/Paths.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceLocalTileServerLogicTest,
    "MantlePlace.Import.LocalTileServerLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLocalTileServerLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceLocalTileServerLogic;

	FString Root = FPaths::ConvertRelativePathToFull(TEXT("/mantleplace/served"));
	FPaths::NormalizeDirectoryName(Root);

	{
		const FString Resolved = FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/14/5615/11520.terrain"));
		TestTrue(TEXT("a quantized-mesh tile resolves"), !Resolved.IsEmpty());
		TestTrue(TEXT("it resolves inside the served root"), Resolved.StartsWith(Root + TEXT("/")));
		TestTrue(TEXT("it names the tile the request asked for"),
			Resolved.EndsWith(TEXT("/Terrain/14/5615/11520.terrain")));

		TestTrue(TEXT("layer.json resolves"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/layer.json"))
				.EndsWith(TEXT("/Terrain/layer.json")));
		TestTrue(TEXT("the imagery raster resolves"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Imagery/Imagery.png"))
				.EndsWith(TEXT("/Imagery/Imagery.png")));
	}

	{
		TestEqual(TEXT("a bare parent reference is refused"),
			FLogic::ResolveUnderRoot(Root, TEXT("/../secret.txt")), FString());
		TestEqual(TEXT("a parent reference after a real directory is refused"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/../../secret.txt")), FString());
		TestEqual(TEXT("a parent reference in the middle is refused"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/../Imagery/Imagery.png")), FString());
		TestEqual(TEXT("a trailing parent reference is refused"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/..")), FString());
	}

	{
		TestEqual(TEXT("an empty path is refused"),
			FLogic::ResolveUnderRoot(Root, FString()), FString());
		TestEqual(TEXT("the root itself is refused — it is a directory, not a file"),
			FLogic::ResolveUnderRoot(Root, TEXT("/")), FString());
		TestEqual(TEXT("an empty root serves nothing"),
			FLogic::ResolveUnderRoot(FString(), TEXT("/Terrain/layer.json")), FString());
	}

	{
		FString Sibling = FPaths::ConvertRelativePathToFull(TEXT("/mantleplace/served-other"));
		FPaths::NormalizeDirectoryName(Sibling);
		const FString Resolved = FLogic::ResolveUnderRoot(Sibling, TEXT("/Terrain/layer.json"));
		TestTrue(TEXT("a sibling root resolves under ITSELF"), Resolved.StartsWith(Sibling + TEXT("/")));
		TestFalse(TEXT("and is not mistaken for a path under the served root"),
			Resolved.StartsWith(Root + TEXT("/")));
	}

	{
		TestEqual(TEXT("a legitimate name containing two dots is refused with the traversals"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/tile..terrain")), FString());
	}

	{
		bool bGzip = false;

		TestEqual(TEXT("a quantized-mesh tile is served as quantized mesh"),
			FLogic::ContentTypeFor(TEXT("/served/Terrain/14/5615/11520.terrain"), bGzip),
			FString(TEXT("application/vnd.quantized-mesh;extensions=octvertexnormals")));
		TestTrue(TEXT("and is declared gzip, because that is how CTB wrote it"), bGzip);

		TestEqual(TEXT("layer.json is json"),
			FLogic::ContentTypeFor(TEXT("/served/Terrain/layer.json"), bGzip),
			FString(TEXT("application/json")));
		TestFalse(TEXT("and is not declared gzip"), bGzip);

		TestEqual(TEXT("the imagery raster is png"),
			FLogic::ContentTypeFor(TEXT("/served/Imagery/Imagery.png"), bGzip),
			FString(TEXT("image/png")));
		TestFalse(TEXT("png is not declared gzip"), bGzip);

		TestEqual(TEXT("jpg is jpeg"),
			FLogic::ContentTypeFor(TEXT("/served/a.jpg"), bGzip), FString(TEXT("image/jpeg")));
		TestEqual(TEXT("jpeg is jpeg"),
			FLogic::ContentTypeFor(TEXT("/served/a.jpeg"), bGzip), FString(TEXT("image/jpeg")));

		bGzip = false;
		TestEqual(TEXT("an upper-case extension is recognised"),
			FLogic::ContentTypeFor(TEXT("/served/Terrain/1.TERRAIN"), bGzip),
			FString(TEXT("application/vnd.quantized-mesh;extensions=octvertexnormals")));
		TestTrue(TEXT("and still declares gzip"), bGzip);

		bGzip = true;
		TestEqual(TEXT("an unknown extension is octet-stream"),
			FLogic::ContentTypeFor(TEXT("/served/notes.txt"), bGzip),
			FString(TEXT("application/octet-stream")));
		TestFalse(TEXT("an unknown extension clears the gzip flag"), bGzip);

		bGzip = true;
		TestEqual(TEXT("no extension at all is octet-stream"),
			FLogic::ContentTypeFor(TEXT("/served/README"), bGzip),
			FString(TEXT("application/octet-stream")));
		TestFalse(TEXT("no extension clears the gzip flag"), bGzip);
	}

	return true;
}

#endif
