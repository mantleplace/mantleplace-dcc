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
	int64& OutDeclaredTileCount)
{
	OutDeclaredTileCount = 0;

	int32 MaxZoom = -1;
	const TMap<int32, FTileSet> ByZoom = GroupByZoom(Tiles, MaxZoom);

	const TArray<TSharedPtr<FJsonValue>>* Declared = nullptr;
	if (!Root.IsValid() || !Root->TryGetArrayField(AvailableField, Declared) || Declared == nullptr)
	{
		// No `available` at all. Cesium treats that as "ask and find out", which is the 404 storm
		// this workaround exists to prevent, so it is not consistent with anything.
		return false;
	}

	// Every level is walked even once a mismatch is known, because the count is what makes the log
	// line an upstream bug report rather than a complaint.
	bool bConsistent = (Declared->Num() == MaxZoom + 1);

	for (int32 Level = 0; Level < Declared->Num(); ++Level)
	{
		const TArray<TSharedPtr<FJsonValue>>* Rects = nullptr;
		if (!(*Declared)[Level].IsValid() || !(*Declared)[Level]->TryGetArray(Rects) || Rects == nullptr)
		{
			bConsistent = false;
			continue;
		}

		FTileSet DeclaredHere;
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
			// not, and this number is only ever printed.
			const int64 Width = static_cast<int64>(EndX) - StartX + 1;
			const int64 Height = static_cast<int64>(EndY) - StartY + 1;
			if (Width <= 0 || Height <= 0)
			{
				bConsistent = false;
				continue;
			}
			OutDeclaredTileCount += Width * Height;

			if (Width != 1 || Height != 1)
			{
				// A rectangle covering more than one tile declares siblings that are not on disk —
				// exactly the shape of the upstream defect. Counted, then judged inconsistent.
				bConsistent = false;
				continue;
			}
			DeclaredHere.Add(MakeTuple(StartX, StartY));
		}

		const FTileSet* Present = ByZoom.Find(Level);
		const int32 PresentNum = Present != nullptr ? Present->Num() : 0;
		if (DeclaredHere.Num() != PresentNum
			|| (Present != nullptr && !DeclaredHere.Includes(*Present)))
		{
			bConsistent = false;
		}
	}

	return bConsistent;
}
