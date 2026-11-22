// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceCesiumAvailabilityLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

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

	{
		FMantlePlaceCesiumTile Parsed;
		TestTrue(TEXT("a tile path parses"),
			FLogic::ParseTilePath(TEXT("/staged/Terrain/14/5615/11520.terrain"), Parsed));
		TestEqual(TEXT("zoom"), Parsed.Zoom, 14);
		TestEqual(TEXT("x"), Parsed.X, 5615);
		TestEqual(TEXT("y"), Parsed.Y, 11520);

		FMantlePlaceCesiumTile Windowsish;
		TestTrue(TEXT("backslashes parse the same"),
			FLogic::ParseTilePath(TEXT("C:\\Saved\\Terrain\\3\\1\\2.terrain"), Windowsish));
		TestEqual(TEXT("zoom, whatever the separator"), Windowsish.Zoom, 3);
		TestEqual(TEXT("x, whatever the separator"), Windowsish.X, 1);
		TestEqual(TEXT("y, whatever the separator"), Windowsish.Y, 2);

		TestTrue(TEXT("zoom 0 parses — it is a real level, not a missing one"),
			FLogic::ParseTilePath(TEXT("Terrain/0/0/0.terrain"), Parsed));
		TestEqual(TEXT("zoom 0"), Parsed.Zoom, 0);

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

	{
		const TArray<FMantlePlaceCesiumTile> Tiles = {
			Tile(0, 0, 0), Tile(1, 1, 0), Tile(2, 2, 1), Tile(2, 3, 1)
		};
		const TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetArrayField(TEXT("available"), FLogic::BuildAvailability(Tiles));

		FMantlePlaceCesiumAvailabilityDiff Diff;
		TestTrue(TEXT("what BuildAvailability writes reads back as consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, Diff));
		TestEqual(TEXT("and declares exactly the tiles present"), Diff.DeclaredTileCount, static_cast<int64>(4));
		TestEqual(TEXT("none of them absent"), Diff.AbsentTileCount, static_cast<int64>(0));
		TestEqual(TEXT("none present left undeclared"), Diff.UndeclaredTileCount, static_cast<int64>(0));
	}

	{
		const TArray<FMantlePlaceCesiumTile> Tiles = {
			Tile(0, 0, 0), Tile(0, 1, 0), Tile(1, 2, 0), Tile(1, 3, 1)
		};
		const TSharedPtr<FJsonObject> Root = MakeLayerJson({
			{ MakeRect(0, 0, 1, 0) },
			{ MakeRect(2, 0, 2, 0), MakeRect(3, 1, 3, 1) },
		});

		FMantlePlaceCesiumAvailabilityDiff Diff;
		TestTrue(TEXT("a multi-tile rectangle whose tiles are all present is consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, Diff));
		TestEqual(TEXT("it declares the four tiles present"), Diff.DeclaredTileCount, static_cast<int64>(4));
		TestEqual(TEXT("none of them absent"), Diff.AbsentTileCount, static_cast<int64>(0));

		TestFalse(TEXT("and no rewrite follows"), FLogic::CorrectAvailability(Root, Tiles, Diff));
		const TArray<TSharedPtr<FJsonValue>>* Available = nullptr;
		const TArray<TSharedPtr<FJsonValue>>* Level0 = nullptr;
		TestTrue(TEXT("the file's own availability is left as it was"),
			Root->TryGetArrayField(TEXT("available"), Available) && Available != nullptr
				&& Available->Num() == 2 && (*Available)[0]->TryGetArray(Level0) && Level0 != nullptr
				&& Level0->Num() == 1);

		const TSharedPtr<FJsonObject> Overlapping = MakeLayerJson({
			{ MakeRect(0, 0, 1, 0), MakeRect(1, 0, 1, 0) },
			{ MakeRect(2, 0, 3, 1) },
		});
		const TArray<FMantlePlaceCesiumTile> Full = {
			Tile(0, 0, 0), Tile(0, 1, 0), Tile(1, 2, 0), Tile(1, 3, 0), Tile(1, 2, 1), Tile(1, 3, 1)
		};
		TestTrue(TEXT("overlapping rectangles over present tiles are consistent"),
			FLogic::IsAvailabilityConsistent(Overlapping, Full, Diff));
		TestEqual(TEXT("and count each tile once"), Diff.DeclaredTileCount, static_cast<int64>(6));
	}

	{
		const TArray<FMantlePlaceCesiumTile> Tiles = { Tile(0, 0, 0), Tile(1, 1, 0) };
		const TSharedPtr<FJsonObject> Root = MakeLayerJson({
			{ MakeRect(0, 0, 0, 0) },
			{ MakeRect(0, 0, 3, 1) },
		});

		FMantlePlaceCesiumAvailabilityDiff Diff;
		TestFalse(TEXT("an over-declared pyramid is not consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, Diff));
		TestEqual(TEXT("and the count says by how much: 1 + 8 declared against 2 present"),
			Diff.DeclaredTileCount, static_cast<int64>(9));
		TestEqual(TEXT("seven of them absent"), Diff.AbsentTileCount, static_cast<int64>(7));

		TestTrue(TEXT("a rectangle declaring an absent tile is rewritten"),
			FLogic::CorrectAvailability(Root, Tiles, Diff));
		FMantlePlaceCesiumAvailabilityDiff After;
		TestTrue(TEXT("and the rewritten file is consistent"),
			FLogic::IsAvailabilityConsistent(Root, Tiles, After));
	}

	{
		const TArray<FMantlePlaceCesiumTile> Tiles = { Tile(0, 0, 0) };
		FMantlePlaceCesiumAvailabilityDiff Diff;
		TestFalse(TEXT("a whole-world rectangle at a deep zoom is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 65535, 32767) } }), Tiles, Diff));
		TestEqual(TEXT("its absent tiles are still counted"),
			Diff.AbsentTileCount, static_cast<int64>(65536) * 32768 - 1);
	}

	{
		const TArray<FMantlePlaceCesiumTile> Tiles = { Tile(0, 0, 0), Tile(1, 1, 0) };
		FMantlePlaceCesiumAvailabilityDiff DeclaredCount;

		TestFalse(TEXT("the right count at the wrong coordinates is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) }, { MakeRect(2, 0, 2, 0) } }),
				Tiles, DeclaredCount));
		TestEqual(TEXT("still counted"), DeclaredCount.DeclaredTileCount, static_cast<int64>(2));
		TestEqual(TEXT("one declared tile is absent"), DeclaredCount.AbsentTileCount, static_cast<int64>(1));
		TestEqual(TEXT("one present tile is undeclared"),
			DeclaredCount.UndeclaredTileCount, static_cast<int64>(1));

		const FString Warning = FLogic::DescribeMismatch(TEXT("job-1"), DeclaredCount, Tiles.Num());
		TestTrue(TEXT("the warning states the declared count"), Warning.Contains(TEXT("declares 2 tiles")));
		TestTrue(TEXT("the warning says how many declared tiles are absent"),
			Warning.Contains(TEXT("1 of them absent")));
		TestTrue(TEXT("the warning states the present count"), Warning.Contains(TEXT("of the 2 tiles")));

		TestFalse(TEXT("a short availability array is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) } }), Tiles, DeclaredCount));

		TestFalse(TEXT("a long availability array is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({
					{ MakeRect(0, 0, 0, 0) }, { MakeRect(1, 0, 1, 0) }, { MakeRect(2, 1, 2, 1) } }),
				Tiles, DeclaredCount));

		TestFalse(TEXT("no availability field is not consistent"),
			FLogic::IsAvailabilityConsistent(MakeShared<FJsonObject>(), Tiles, DeclaredCount));
		TestEqual(TEXT("and declares nothing"), DeclaredCount.DeclaredTileCount, static_cast<int64>(0));

		TestFalse(TEXT("a null root is not consistent"),
			FLogic::IsAvailabilityConsistent(TSharedPtr<FJsonObject>(), Tiles, DeclaredCount));

		const TSharedPtr<FJsonObject> Partial = MakeShared<FJsonObject>();
		Partial->SetNumberField(TEXT("startX"), 0);
		TestFalse(TEXT("a rectangle missing its bounds is not consistent"),
			FLogic::IsAvailabilityConsistent(
				MakeLayerJson({ { MakeRect(0, 0, 0, 0) }, { Partial } }), Tiles, DeclaredCount));
	}

	return true;
}

#endif
