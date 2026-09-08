// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCesiumAvailabilityLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

// The `layer.json` availability workaround, asserted where it can be asserted.
//
// The workaround corrects an upstream packaging defect: the bundle declares the whole-world pyramid
// at low zooms while shipping one AOI-ancestor tile per level, and Cesium — which trusts `available`
// — 404s its way to "Errors loading quantized mesh terrain" and renders nothing. Whether the defect
// is still there is a question about bundles, not about source, so the consistency check exists to
// let a real bundle answer it in the log. These assertions are about the check itself.

namespace
{
	TSharedPtr<FJsonObject> MakeRect(int32 StartX, int32 StartY, int32 EndX, int32 EndY)
	{
		const TSharedPtr<FJsonObject> Rect = MakeShared<FJsonObject>();
		Rect->SetNumberField(TEXT("startX"), StartX);
		Rect->SetNumberField(TEXT("startY"), StartY);
		Rect->SetNumberField(TEXT("endX"), EndX);
		Rect->SetNumberField(TEXT("endY"), EndY);
		return Rect;
	}

	/** A layer.json root carrying `available` exactly as given, level by level. */
	TSharedPtr<FJsonObject> MakeLayerJson(const TArray<TArray<TSharedPtr<FJsonObject>>>& Levels)
	{
		TArray<TSharedPtr<FJsonValue>> Available;
		for (const TArray<TSharedPtr<FJsonObject>>& Level : Levels)
		{
			TArray<TSharedPtr<FJsonValue>> Rects;
			for (const TSharedPtr<FJsonObject>& Rect : Level)
			{
				Rects.Add(MakeShared<FJsonValueObject>(Rect));
			}
			Available.Add(MakeShared<FJsonValueArray>(Rects));
		}
		const TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetArrayField(TEXT("available"), Available);
		return Root;
	}

	FMantlePlaceCesiumTile Tile(int32 Zoom, int32 X, int32 Y)
	{
		FMantlePlaceCesiumTile Result;
		Result.Zoom = Zoom;
		Result.X = X;
		Result.Y = Y;
		return Result;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceCesiumAvailabilityLogicTest,
    "MantlePlace.Import.CesiumAvailabilityLogic",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceCesiumAvailabilityLogicTest::RunTest(const FString& Parameters)
{
	using FLogic = FMantlePlaceCesiumAvailabilityLogic;

	// --- Reading {z}/{x}/{y} off a tile path ----------------------------------------------------
	{
		FMantlePlaceCesiumTile Parsed;
		TestTrue(TEXT("a tile path parses"),
			FLogic::ParseTilePath(TEXT("/staged/Terrain/14/5615/11520.terrain"), Parsed));
		TestEqual(TEXT("zoom"), Parsed.Zoom, 14);
		TestEqual(TEXT("x"), Parsed.X, 5615);
		TestEqual(TEXT("y"), Parsed.Y, 11520);

		// Read from the trailing components, so the directory above the tile tree is irrelevant and
		// so is separator style.
		FMantlePlaceCesiumTile Windowsish;
		TestTrue(TEXT("backslashes parse the same"),
			FLogic::ParseTilePath(TEXT("C:\\Saved\\Terrain\\3\\1\\2.terrain"), Windowsish));
		TestEqual(TEXT("zoom, whatever the separator"), Windowsish.Zoom, 3);
		TestEqual(TEXT("x, whatever the separator"), Windowsish.X, 1);
		TestEqual(TEXT("y, whatever the separator"), Windowsish.Y, 2);

		TestTrue(TEXT("zoom 0 parses — it is a real level, not a missing one"),
			FLogic::ParseTilePath(TEXT("Terrain/0/0/0.terrain"), Parsed));
		TestEqual(TEXT("zoom 0"), Parsed.Zoom, 0);

		// Digits only. FString::IsNumeric would accept every one of these, and Atoi would turn the
		// first into tile 1 — a tile that exists, at a coordinate nothing on disk occupies.
		FMantlePlaceCesiumTile Rejected;
		TestFalse(TEXT("a decimal is not a tile coordinate"),
			FLogic::ParseTilePath(TEXT("Terrain/1.5/1/2.terrain"), Rejected));
		TestFalse(TEXT("a signed number is not a tile coordinate"),
			FLogic::ParseTilePath(TEXT("Terrain/-1/1/2.terrain"), Rejected));
		TestFalse(TEXT("a non-numeric component is not a tile coordinate"),
			FLogic::ParseTilePath(TEXT("Terrain/layer/1/2.terrain"), Rejected));
		TestFalse(TEXT("a path with too few components is not a tile"),
			FLogic::ParseTilePath(TEXT("11520.terrain"), Rejected));
	}

	// --- The availability an AOI descent chain should carry -------------------------------------
	// The real shape: one tile per level, each the parent of the next, down to the AOI's own zoom.
	{
		const TArray<FMantlePlaceCesiumTile> Tiles = {
			Tile(0, 0, 0), Tile(1, 1, 0), Tile(2, 2, 1)
		};
		const TArray<TSharedPtr<FJsonValue>> Available = FLogic::BuildAvailability(Tiles);
		TestEqual(TEXT("one entry per level, 0 through the deepest"), Available.Num(), 3);

		const TArray<TSharedPtr<FJsonValue>>* Level2 = nullptr;
		if (Available.Num() == 3 && Available[2]->TryGetArray(Level2) && Level2 != nullptr)
		{
			TestEqual(TEXT("the deepest level lists its one tile"), Level2->Num(), 1);
			const TSharedPtr<FJsonObject>* Rect = nullptr;
			if (Level2->Num() == 1 && (*Level2)[0]->TryGetObject(Rect) && Rect != nullptr)
			{
				// 1x1 and inclusive. A rectangle claiming more would re-create the upstream defect.
				TestEqual(TEXT("startX"), (*Rect)->GetIntegerField(TEXT("startX")), 2);
				TestEqual(TEXT("endX"), (*Rect)->GetIntegerField(TEXT("endX")), 2);
				TestEqual(TEXT("startY"), (*Rect)->GetIntegerField(TEXT("startY")), 1);
				TestEqual(TEXT("endY"), (*Rect)->GetIntegerField(TEXT("endY")), 1);
			}
		}
		else
		{
			AddError(TEXT("the deepest level is not an array"));
		}

		// A level with no tiles still gets an entry: the array is indexed by level, so a gap would
		// shift every level below it and misdeclare the lot.
		const TArray<FMantlePlaceCesiumTile> Gapped = { Tile(0, 0, 0), Tile(2, 2, 1) };
		const TArray<TSharedPtr<FJsonValue>> WithGap = FLogic::BuildAvailability(Gapped);
		TestEqual(TEXT("a missing level still occupies its index"), WithGap.Num(), 3);
		const TArray<TSharedPtr<FJsonValue>>* Empty = nullptr;
		if (WithGap.Num() == 3 && WithGap[1]->TryGetArray(Empty) && Empty != nullptr)
		{
			TestEqual(TEXT("and lists nothing"), Empty->Num(), 0);
		}

		TestEqual(TEXT("no tiles is no availability"),
			FLogic::BuildAvailability(TArray<FMantlePlaceCesiumTile>()).Num(), 0);
	}

	// --- What this rewrite writes is, by construction, consistent -------------------------------
	// The round trip is the assertion that matters: if it did not hold, the rewrite would rewrite
	// on every stream forever and the "already consistent" signal would never fire even once the
	// pipeline is fixed.
	{
		const TArray<FMantlePlaceCesiumTile> Tiles = {
			Tile(0, 0, 0), Tile(1, 1, 0), Tile(2, 2, 1), Tile(2, 3, 1)
		};
		const TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetArrayField(TEXT("available"), FLogic::BuildAvailability(Tiles));

		int64 DeclaredCount = 0;
		TestTrue(TEXT("what BuildAvailability writes reads back as consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, DeclaredCount));
		TestEqual(TEXT("and declares exactly the tiles present"), DeclaredCount, static_cast<int64>(4));
	}

	// --- The upstream defect, as reported --------------------------------------------------------
	// Level 1 claims x[0..3] y[0..1] — eight tiles — while one is on disk. This is the case the
	// workaround exists for, and the count is the evidence an upstream report needs.
	{
		const TArray<FMantlePlaceCesiumTile> Tiles = { Tile(0, 0, 0), Tile(1, 1, 0) };
		const TSharedPtr<FJsonObject> Root = MakeLayerJson({
			{ MakeRect(0, 0, 0, 0) },
			{ MakeRect(0, 0, 3, 1) },
		});

		int64 DeclaredCount = 0;
		TestFalse(TEXT("an over-declared pyramid is not consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, DeclaredCount));
		TestEqual(TEXT("and the count says by how much: 1 + 8 declared against 2 present"),
			DeclaredCount, static_cast<int64>(9));
	}

	// --- The other ways a layer.json can disagree ------------------------------------------------
	{
		const TArray<FMantlePlaceCesiumTile> Tiles = { Tile(0, 0, 0), Tile(1, 1, 0) };
		int64 DeclaredCount = 0;

		// The right number of tiles, at the wrong coordinates. A count-only check would pass this
		// and Cesium would 404 every request.
		TestFalse(TEXT("the right count at the wrong coordinates is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) }, { MakeRect(2, 0, 2, 0) } }),
				Tiles, DeclaredCount));
		TestEqual(TEXT("still counted"), DeclaredCount, static_cast<int64>(2));

		// Fewer levels than there are tiles.
		TestFalse(TEXT("a short availability array is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) } }), Tiles, DeclaredCount));

		// More levels than there are tiles.
		TestFalse(TEXT("a long availability array is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({
					{ MakeRect(0, 0, 0, 0) }, { MakeRect(1, 0, 1, 0) }, { MakeRect(2, 1, 2, 1) } }),
				Tiles, DeclaredCount));

		// No `available` at all: Cesium asks and finds out, which is the 404 storm itself.
		TestFalse(TEXT("no availability field is not consistent"),
			FLogic::IsAvailabilityConsistent(MakeShared<FJsonObject>(), Tiles, DeclaredCount));
		TestEqual(TEXT("and declares nothing"), DeclaredCount, static_cast<int64>(0));

		// An invalid root is not a special case worth crashing over.
		TestFalse(TEXT("a null root is not consistent"),
			FLogic::IsAvailabilityConsistent(TSharedPtr<FJsonObject>(), Tiles, DeclaredCount));

		// A malformed rectangle is a file we do not understand, so it is not one we can call correct.
		const TSharedPtr<FJsonObject> Partial = MakeShared<FJsonObject>();
		Partial->SetNumberField(TEXT("startX"), 0);
		TestFalse(TEXT("a rectangle missing its bounds is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) }, { Partial } }), Tiles, DeclaredCount));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
