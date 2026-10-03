// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceCesiumAvailabilityLogic.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Misc/Paths.h"

namespace
{
	const TCHAR* const AvailableField = TEXT("available");
	const TCHAR* const StartXField = TEXT("startX");
	const TCHAR* const StartYField = TEXT("startY");
	const TCHAR* const EndXField = TEXT("endX");
	const TCHAR* const EndYField = TEXT("endY");

	/** Unsigned integer, digits only — see the note on ParseTilePath. */
	bool IsUnsignedInteger(const FString& Text)
	{
		if (Text.IsEmpty())
		{
			return false;
		}
		for (const TCHAR Char : Text)
		{
			if (!FChar::IsDigit(Char))
			{
				return false;
			}
		}
		return true;
	}

	/** The tiles of one zoom level, as a set, for comparing against what a level declares. */
	using FTileSet = TSet<TTuple<int32, int32>>;

	TMap<int32, FTileSet> GroupByZoom(const TArray<FMantlePlaceCesiumTile>& Tiles, int32& OutMaxZoom)
	{
		OutMaxZoom = -1;
		TMap<int32, FTileSet> ByZoom;
		for (const FMantlePlaceCesiumTile& Tile : Tiles)
		{
			ByZoom.FindOrAdd(Tile.Zoom).Add(MakeTuple(Tile.X, Tile.Y));
			OutMaxZoom = FMath::Max(OutMaxZoom, Tile.Zoom);
		}
		return ByZoom;
	}
}

bool FMantlePlaceCesiumAvailabilityLogic::ParseTilePath(
	const FString& TilePath, FMantlePlaceCesiumTile& OutTile)
{
	const FString YStr = FPaths::GetBaseFilename(TilePath);              // {y}
	const FString XDir = FPaths::GetPath(TilePath);                      // .../{z}/{x}
	const FString XStr = FPaths::GetCleanFilename(XDir);                 // {x}
	const FString ZStr = FPaths::GetCleanFilename(FPaths::GetPath(XDir)); // {z}
	if (!IsUnsignedInteger(ZStr) || !IsUnsignedInteger(XStr) || !IsUnsignedInteger(YStr))
	{
		return false;
	}

	OutTile.Zoom = FCString::Atoi(*ZStr);
	OutTile.X = FCString::Atoi(*XStr);
	OutTile.Y = FCString::Atoi(*YStr);
	return true;
}

TArray<TSharedPtr<FJsonValue>> FMantlePlaceCesiumAvailabilityLogic::BuildAvailability(
	const TArray<FMantlePlaceCesiumTile>& Tiles)
{
	int32 MaxZoom = -1;
	const TMap<int32, FTileSet> ByZoom = GroupByZoom(Tiles, MaxZoom);

	TArray<TSharedPtr<FJsonValue>> Available;
	if (MaxZoom < 0)
	{
		return Available;
	}

	Available.Reserve(MaxZoom + 1);
	for (int32 Zoom = 0; Zoom <= MaxZoom; ++Zoom)
	{
		TArray<TSharedPtr<FJsonValue>> LevelRects;
		if (const FTileSet* Level = ByZoom.Find(Zoom))
		{
			// Sorted, so the written file is a deterministic function of the tiles on disk: a diff of
			// two rewrites of the same bundle is empty, and the reuse check upstream means something.
			TArray<TTuple<int32, int32>> Sorted = Level->Array();
			Sorted.Sort([](const TTuple<int32, int32>& A, const TTuple<int32, int32>& B)
				{
					return A.Get<0>() != B.Get<0>() ? A.Get<0>() < B.Get<0>() : A.Get<1>() < B.Get<1>();
				});
			LevelRects.Reserve(Sorted.Num());
			for (const TTuple<int32, int32>& XY : Sorted)
			{
				const TSharedPtr<FJsonObject> Rect = MakeShared<FJsonObject>();
				Rect->SetNumberField(StartXField, XY.Get<0>());
				Rect->SetNumberField(StartYField, XY.Get<1>());
				Rect->SetNumberField(EndXField, XY.Get<0>());
				Rect->SetNumberField(EndYField, XY.Get<1>());
				LevelRects.Add(MakeShared<FJsonValueObject>(Rect));
			}
		}
		Available.Add(MakeShared<FJsonValueArray>(LevelRects));
	}
	return Available;
}

bool FMantlePlaceCesiumAvailabilityLogic::IsAvailabilityConsistent(
	const TSharedPtr<FJsonObject>& Root,
	const TArray<FMantlePlaceCesiumTile>& Tiles,
	FMantlePlaceCesiumAvailabilityDiff& OutDiff)
{
	OutDiff = FMantlePlaceCesiumAvailabilityDiff();

	int32 MaxZoom = -1;
	const TMap<int32, FTileSet> ByZoom = GroupByZoom(Tiles, MaxZoom);

	const TArray<TSharedPtr<FJsonValue>>* Declared = nullptr;
	if (!Root.IsValid() || !Root->TryGetArrayField(AvailableField, Declared) || Declared == nullptr)
	{
		// No `available` at all. Cesium treats that as "ask and find out", which is the 404 storm
		// this workaround exists to prevent, so it is not consistent with anything.
		OutDiff.UndeclaredTileCount = Tiles.Num();
		return false;
	}

	// Every level is walked even once a mismatch is known, because the counts are what make the log
	// line an upstream bug report rather than a complaint.
	bool bConsistent = (Declared->Num() == MaxZoom + 1);

	// Present tiles at a zoom the file has no entry for are undeclared, whatever the rest says.
	for (const TPair<int32, FTileSet>& Level : ByZoom)
	{
		if (Level.Key >= Declared->Num())
		{
			OutDiff.UndeclaredTileCount += Level.Value.Num();
		}
	}

	const FTileSet NoTiles;
	for (int32 Level = 0; Level < Declared->Num(); ++Level)
	{
		const FTileSet* PresentPtr = ByZoom.Find(Level);
		const FTileSet& Present = PresentPtr != nullptr ? *PresentPtr : NoTiles;

		const TArray<TSharedPtr<FJsonValue>>* Rects = nullptr;
		if (!(*Declared)[Level].IsValid() || !(*Declared)[Level]->TryGetArray(Rects) || Rects == nullptr)
		{
			bConsistent = false;
			OutDiff.UndeclaredTileCount += Present.Num();
			continue;
		}

		// Read every rectangle first, so the level's size is known before anything is expanded.
		struct FRect
		{
			int64 StartX, StartY, EndX, EndY;
			bool Covers(const TTuple<int32, int32>& XY) const
			{
				return XY.Get<0>() >= StartX && XY.Get<0>() <= EndX
					&& XY.Get<1>() >= StartY && XY.Get<1>() <= EndY;
			}
			int64 Area() const { return (EndX - StartX + 1) * (EndY - StartY + 1); }
		};
		TArray<FRect> LevelRects;
		int64 LevelArea = 0;
		for (const TSharedPtr<FJsonValue>& RectValue : *Rects)
		{
			const TSharedPtr<FJsonObject>* Rect = nullptr;
			if (!RectValue.IsValid() || !RectValue->TryGetObject(Rect) || Rect == nullptr)
			{
				bConsistent = false;
				continue;
			}

			int32 StartX = 0, StartY = 0, EndX = 0, EndY = 0;
			if (!(*Rect)->TryGetNumberField(StartXField, StartX)
				|| !(*Rect)->TryGetNumberField(StartYField, StartY)
				|| !(*Rect)->TryGetNumberField(EndXField, EndX)
				|| !(*Rect)->TryGetNumberField(EndYField, EndY))
			{
				bConsistent = false;
				continue;
			}

			// Inclusive on both ends, which is what makes the whole-world claim eight tiles rather
			// than three. int64 because a low-zoom whole-world rectangle is small but a deep one is
			// not.
			const FRect Parsed{ StartX, StartY, EndX, EndY };
			if (Parsed.EndX < Parsed.StartX || Parsed.EndY < Parsed.StartY)
			{
				bConsistent = false;
				continue;
			}
			LevelRects.Add(Parsed);
			LevelArea += Parsed.Area();
		}

		int64 UndeclaredHere = 0;
		for (const TTuple<int32, int32>& XY : Present)
		{
			if (!LevelRects.ContainsByPredicate([&XY](const FRect& R) { return R.Covers(XY); }))
			{
				++UndeclaredHere;
			}
		}
		OutDiff.UndeclaredTileCount += UndeclaredHere;

		// A bundle's levels hold a handful of tiles, so a level declaring more than this many is
		// already far from what is on disk. Expanding it would cost memory to learn nothing, so its
		// absent tiles are counted per rectangle instead — exact unless two rectangles overlap.
		constexpr int64 ExpansionLimit = 1 << 20;
		int64 AbsentHere = 0;
		if (LevelArea > ExpansionLimit)
		{
			bConsistent = false;
			OutDiff.DeclaredTileCount += LevelArea;
			for (const FRect& R : LevelRects)
			{
				int64 PresentInRect = 0;
				for (const TTuple<int32, int32>& XY : Present)
				{
					PresentInRect += R.Covers(XY) ? 1 : 0;
				}
				AbsentHere += R.Area() - PresentInRect;
			}
		}
		else
		{
			FTileSet DeclaredHere;
			for (const FRect& R : LevelRects)
			{
				for (int64 X = R.StartX; X <= R.EndX; ++X)
				{
					for (int64 Y = R.StartY; Y <= R.EndY; ++Y)
					{
						DeclaredHere.Add(MakeTuple(static_cast<int32>(X), static_cast<int32>(Y)));
					}
				}
			}
			OutDiff.DeclaredTileCount += DeclaredHere.Num();
			for (const TTuple<int32, int32>& XY : DeclaredHere)
			{
				AbsentHere += Present.Contains(XY) ? 0 : 1;
			}
		}
		OutDiff.AbsentTileCount += AbsentHere;

		if (AbsentHere != 0 || UndeclaredHere != 0)
		{
			bConsistent = false;
		}
	}

	return bConsistent;
}

bool FMantlePlaceCesiumAvailabilityLogic::CorrectAvailability(
	const TSharedPtr<FJsonObject>& Root,
	const TArray<FMantlePlaceCesiumTile>& Tiles,
	FMantlePlaceCesiumAvailabilityDiff& OutDiff)
{
	if (IsAvailabilityConsistent(Root, Tiles, OutDiff) || !Root.IsValid())
	{
		return false;
	}
	Root->SetArrayField(AvailableField, BuildAvailability(Tiles));
	return true;
}

FString FMantlePlaceCesiumAvailabilityLogic::DescribeMismatch(
	const FString& JobId, const FMantlePlaceCesiumAvailabilityDiff& Diff, int32 PresentTileCount)
{
	return FString::Printf(
		TEXT("Cesium terrain layer.json for job %s declares %lld tiles, %lld of them absent from the "
			 "bundle, and leaves %lld of the %d tiles the bundle ships undeclared. Rewriting "
			 "availability so Cesium does not 404 its way to a load error. This is an upstream "
			 "packaging defect in the bundle, not an import fault — report it with this line."),
		*JobId, Diff.DeclaredTileCount, Diff.AbsentTileCount, Diff.UndeclaredTileCount,
		PresentTileCount);
}
