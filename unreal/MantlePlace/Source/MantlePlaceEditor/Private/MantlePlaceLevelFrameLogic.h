// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * One bundle's own frame origin, exactly as its `hosts.unreal` block publishes it (and the vertical
 * datum its `location.site_origin` names). Nothing here is computed: every field is copied from the
 * manifest, and the only arithmetic this unit does is the difference of two published easting/northing
 * pairs.
 */
struct FMantlePlaceFrameOrigin
{
	/** hosts.unreal.georeference.crs_projected, verbatim ("EPSG:32613"). Compared as a string. */
	FString Crs;
	/** location.site_origin.vertical_datum, verbatim ("NAVD88 (GEOID18)"). Compared as a string. */
	FString VerticalDatum;
	/** hosts.unreal.georeference.origin.utm easting / northing, metres. */
	double EastingM = 0.0;
	double NorthingM = 0.0;
	/** False when the manifest published no origin to read (the numbers above are then not a statement). */
	bool bStated = false;
};

/**
 * What an earlier import left on its actors: the origin it was placed from and the horizontal offset it
 * was given, in metres. `bPlaced` is false for an import that was centred on the world origin because it
 * could not be placed against the level's anchor; such an import says nothing about where the anchor is.
 */
struct FMantlePlaceFrameRecord
{
	bool bPlaced = false;
	FMantlePlaceFrameOrigin Origin;
	double OffsetEastM = 0.0;
	double OffsetNorthM = 0.0;
};

/** One import found in the level: its identity (from its import tag) and the value of its frame tag, if any. */
struct FMantlePlaceLevelImportTag
{
	FString Identity;
	/** The frame tag's value (after its prefix); empty for an import made before frames were recorded. */
	FString FrameValue;
};

enum class EMantlePlaceFrameOutcome : uint8
{
	/** Nothing placed in the level to measure against: this import is the anchor, at the world origin. */
	Anchor,
	/** Placed at its published offset from the anchor. */
	Offset,
	/** Imported centred on the world origin, as before this feature, for the reason in `Refusal`. */
	Centred,
};

enum class EMantlePlaceFrameRefusal : uint8
{
	None,
	/** This bundle states no origin or no `crs_projected`. */
	OriginUnstated,
	/** The anchor's `crs_projected` string is not this bundle's. */
	CrsDiffers,
	/** Either bundle states no vertical datum, or the two strings differ. */
	DatumDiffers,
	/** The offset from the anchor is beyond FMantlePlaceLevelFrameLogic::MaxOffsetM. */
	TooFar,
};

struct FMantlePlaceFramePlan
{
	EMantlePlaceFrameOutcome Outcome = EMantlePlaceFrameOutcome::Anchor;
	EMantlePlaceFrameRefusal Refusal = EMantlePlaceFrameRefusal::None;

	/** The horizontal offset to apply, metres. East and north, as the manifest's own grid names them. */
	double OffsetEastM = 0.0;
	double OffsetNorthM = 0.0;

	/** One WARNING line naming the reason, set only when `Outcome` is Centred because of a refusal. */
	FString Warning;
	/** One information line, set when the import is the anchor while other imports sit outside a shared frame. */
	FString Note;
	/** The frame tag value every actor of this import carries, so a later import reads it. */
	FString FrameValue;

	bool HasOffset() const { return Outcome == EMantlePlaceFrameOutcome::Offset && (OffsetEastM != 0.0 || OffsetNorthM != 0.0); }
};

/**
 * Where a new import goes when the level already holds Mantle Place imports -- a decision, so it lives
 * here with a headless test (MantlePlace.Import.LevelFrameLogic) rather than in the import code.
 *
 * The level's ANCHOR is a point, and nothing stores it by itself: every placed import records its own
 * origin and the offset it was given, and `origin - offset` is the anchor's origin from any one of them.
 * So the anchor is the first import's origin, a third bundle lands against it, re-importing any bundle
 * recomputes the offset it had (the anchor's own stays zero), and with no placed import left in the
 * level there is no record and so no anchor -- it clears by itself.
 *
 * Offset only on string equality of `crs_projected` and of the vertical datum: the plugin never works
 * out a zone and never converts a datum (spec section 6). Anything else is imported centred, as it was
 * before this feature, with one warning that names the reason.
 */
class FMantlePlaceLevelFrameLogic
{
public:
	/**
	 * The largest horizontal offset from the anchor, in metres, that is applied (the larger of the east
	 * and north distances), measured origin to origin: the far edge of a bundle sits half the bundle's
	 * extent further out than the origin this cap bounds. 100 km is where a 32-bit float stops resolving a centimetre: float spacing is
	 * 1 cm from 2^23 cm (83.9 km) to 2^24 cm. The editor, physics and several tools still hold positions
	 * in single precision, so past it the seam this feature exists to close would be lost in the noise of
	 * the world position itself. The cap is a refusal threshold; it derives nothing.
	 */
	static constexpr double MaxOffsetM = 100000.0;

	/** The tag value written for an import: `v1|placed|<crs>|<datum>|<E>|<N>|<offE>|<offN>` or `v1|centred|...`. */
	static FString EncodeFrame(const FMantlePlaceFrameRecord& Record);

	/** Reads a value EncodeFrame wrote. False for an empty, malformed or unknown-version value. */
	static bool DecodeFrame(const FString& Value, FMantlePlaceFrameRecord& OutRecord);

	/**
	 * The plan for importing `ThisOrigin` under `ThisIdentity` into a level holding `InLevel`. The level's
	 * entries for ThisIdentity itself are ignored: that import is being replaced.
	 */
	static FMantlePlaceFramePlan Plan(
		const FString& ThisIdentity, const FMantlePlaceFrameOrigin& ThisOrigin,
		const TArray<FMantlePlaceLevelImportTag>& InLevel);
};
