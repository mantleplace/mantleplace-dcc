// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleReaderLogic.h"

struct FMantlePlaceVaultManifest;

/** The engine's layout for a heightmap of a stated size: the one a New Landscape of that size takes. */
struct FMantlePlaceLandscapeLayout
{
	int32 SectionSizeQuads = 0;
	int32 SectionsPerComponent = 0;
	int32 ComponentCountX = 0; // along the raster's width (east)
	int32 ComponentCountY = 0; // along its height (north)
};

/**
 * Pure logic for importing an MPB 3.x bundle: what the 3.0 reader found (FMantlePlaceBundleReaderLogic)
 * and the manifest's text, turned into the FMantlePlaceVaultManifest every artifact handler already
 * imports from. The handlers, the integrity chain and the orchestration do not change; this unit only
 * says, per step, which promised file feeds it and how that file's stated values read in this host.
 *
 *  - **Every file is found by its promised name in the catalog** (spec/format.md §9.5), at the archive
 *    entry the reader found it at, and is placed in its family's frame. Unreal places host-ready files
 *    only: each one is in the local site frame, in metres, whose origin (the manifest's site origin,
 *    stated in the projected CRS whose grid the frame's axes follow) becomes this host's georeference
 *    origin. A heightmap's height is `height of the site origin + offset + code × scale`; every stated
 *    number is multiplied into centimetres and nothing else (HPS-33).
 *  - **The engine layout is the engine's.** 3.0 drops the section size and component counts: the
 *    producer writes `Elevation.png` at a size the layout accepts, and ChooseLandscapeLayout picks the
 *    layout a New Landscape of that size takes.
 *  - **A step whose 2.x input has no 3.0 host-ready file is skipped with that reason** (OutManifest's
 *    FormatSkips), and so is a step whose file this download does not hold, with the manifest's stated
 *    reason shown verbatim (format.md §9.6). Unreal never converts a georeferenced file.
 *  - **Fail closed on a unit, axis or frame it does not understand** (format.md §4), naming the value: a
 *    site frame not in metres refuses the bundle; a raster whose frame or shape is not stated skips its step.
 */
struct FMantlePlaceManifestThreeLogic
{
	/** The catalog names this host imports, one per step (spec/format.md §6). */
	static const TCHAR* HeightmapPath() { return TEXT("Elevation/Elevation.png"); }
	static const TCHAR* DrapePath() { return TEXT("Imagery/Imagery.png"); }
	static const TCHAR* TerrainMeshPath() { return TEXT("Elevation/Terrain.glb"); }
	static const TCHAR* BuildingsPath() { return TEXT("Basemap/Buildings.glb"); }
	static const TCHAR* TreesPath() { return TEXT("Landcover/Trees.csv"); }
	static const TCHAR* RoadsPath() { return TEXT("Basemap/Roads.csv"); }
	/** The folder of the weight maps: every PNG in it is one paint layer, named by its file (format.md §6). */
	static const TCHAR* MaterialsFolder() { return TEXT("Materials/"); }

	/**
	 * Fill `OutManifest` from a 3.x read and its manifest text. False, with OutError naming the value,
	 * when the manifest is not one this host can place at all (no site origin, a frame not in metres, an
	 * axis it does not know); every per-step problem is a named skip instead.
	 */
	static bool ToVaultManifest(const FMantlePlaceBundleRead& Read, const FString& ManifestJson,
		FMantlePlaceVaultManifest& OutManifest, FString& OutError);

	/**
	 * The layout for a `Width` × `Height` heightmap: the first of the engine's section sizes, in the order
	 * a New Landscape prefers them (63 quads in 2 sections first), whose components tile both sides
	 * exactly with at most 32 per side. False when none does.
	 */
	static bool ChooseLandscapeLayout(int32 Width, int32 Height, FMantlePlaceLandscapeLayout& OutLayout);

	/** The words a step skipped for a 3.0 bundle shows, one per reason (also the release notes' list). */
	static FString NoHostReadyFileReason(const FString& What);
	static FString NotHeldReason(const FString& Path, const FString& StatedReason);
};
