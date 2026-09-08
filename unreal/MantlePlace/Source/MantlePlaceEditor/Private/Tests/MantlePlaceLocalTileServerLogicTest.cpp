// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceLocalTileServerLogic.h"

#include "Misc/Paths.h"

// The local tile server hosts an extracted bundle on loopback so Cesium for Unreal can read the
// user's own download. Its request handler is a catch-all preprocessor: every path a client sends
// arrives here, and the only thing between that path and the rest of the machine's filesystem is
// the containment check asserted below.
//
// None of this needs a router, a bound port or a directory on disk, which is why it can be asserted
// at all. The server itself still cannot be tested here — it needs the HTTP module and a real
// listener — and that is exactly the split: the shim binds and reads, the logic decides.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceLocalTileServerLogicTest,
    "MantlePlace.Import.LocalTileServerLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceLocalTileServerLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceLocalTileServerLogic;

	// Built the way the server builds it at Start(), so the assertions hold on every platform rather
	// than only on the one that spells absolute paths this way.
	FString Root = FPaths::ConvertRelativePathToFull(TEXT("/mantleplace/served"));
	FPaths::NormalizeDirectoryName(Root);

	// --- A tile path resolves under the root ---------------------------------------------------
	{
		const FString Resolved = FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/14/5615/11520.terrain"));
		TestTrue(TEXT("a quantized-mesh tile resolves"), !Resolved.IsEmpty());
		TestTrue(TEXT("it resolves inside the served root"), Resolved.StartsWith(Root + TEXT("/")));
		TestTrue(TEXT("it names the tile the request asked for"),
			Resolved.EndsWith(TEXT("/Terrain/14/5615/11520.terrain")));

		// The two other things the bundle serves, so the arbitrary {z}/{x}/{y} depth above is not
		// the only shape covered.
		TestTrue(TEXT("layer.json resolves"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/layer.json"))
				.EndsWith(TEXT("/Terrain/layer.json")));
		TestTrue(TEXT("the imagery raster resolves"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Imagery/Imagery.png"))
				.EndsWith(TEXT("/Imagery/Imagery.png")));
	}

	// --- Traversal is refused ------------------------------------------------------------------
	// This is the assertion the split was made for. Every one of these is a path a client can send.
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

	// --- Nothing to serve ----------------------------------------------------------------------
	{
		TestEqual(TEXT("an empty path is refused"),
			FLogic::ResolveUnderRoot(Root, FString()), FString());
		TestEqual(TEXT("the root itself is refused — it is a directory, not a file"),
			FLogic::ResolveUnderRoot(Root, TEXT("/")), FString());
		TestEqual(TEXT("an empty root serves nothing"),
			FLogic::ResolveUnderRoot(FString(), TEXT("/Terrain/layer.json")), FString());
	}

	// --- Two roots do not bleed into each other -------------------------------------------------
	// `<root>-other` shares the served root's spelling as a prefix. The `..` rule above means no
	// request can actually walk from one to the other today, so this is belt-and-braces — but the
	// trailing slash in the containment guard is what makes it so, and it is also what refuses the
	// root itself above. Asserted so a future loosening of the `..` rule cannot quietly remove both.
	{
		FString Sibling = FPaths::ConvertRelativePathToFull(TEXT("/mantleplace/served-other"));
		FPaths::NormalizeDirectoryName(Sibling);
		const FString Resolved = FLogic::ResolveUnderRoot(Sibling, TEXT("/Terrain/layer.json"));
		TestTrue(TEXT("a sibling root resolves under ITSELF"), Resolved.StartsWith(Sibling + TEXT("/")));
		TestFalse(TEXT("and is not mistaken for a path under the served root"),
			Resolved.StartsWith(Root + TEXT("/")));
	}

	// --- The `..` rule is cruder than containment, on purpose ------------------------------------
	// A file whose own name contains two dots is refused even though it is inside the root. Recorded
	// as a deliberate trade rather than left to be discovered: the served tree is machine-generated
	// by the pipeline, so nothing that should be served carries such a name, and a traversal that
	// never reaches the path arithmetic cannot be let through by a mistake in it.
	{
		TestEqual(TEXT("a legitimate name containing two dots is refused with the traversals"),
			FLogic::ResolveUnderRoot(Root, TEXT("/Terrain/tile..terrain")), FString());
	}

	// --- Content types --------------------------------------------------------------------------
	{
		bool bGzip = false;

		TestEqual(TEXT("a quantized-mesh tile is served as quantized mesh"),
			FLogic::ContentTypeFor(TEXT("/served/Terrain/14/5615/11520.terrain"), bGzip),
			FString(TEXT("application/vnd.quantized-mesh;extensions=octvertexnormals")));
		// The load-bearing half: CTB writes these gzipped on disk. Declared, never re-compressed.
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

		// Extensions arrive from a request path, so case is not ours to assume.
		bGzip = false;
		TestEqual(TEXT("an upper-case extension is recognised"),
			FLogic::ContentTypeFor(TEXT("/served/Terrain/1.TERRAIN"), bGzip),
			FString(TEXT("application/vnd.quantized-mesh;extensions=octvertexnormals")));
		TestTrue(TEXT("and still declares gzip"), bGzip);

		// Unknown is octet-stream rather than a guess: a wrong content type is worse than none.
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

#endif // WITH_DEV_AUTOMATION_TESTS
