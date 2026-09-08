// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

/**
 * Which of the drape-alignment descriptor's shapes `hosts.unreal.imagery_drape.alignment` carries.
 *
 * The published schema states the field is deliberately a FREE STRING and not an enum, because the
 * divergent shape embeds measured numbers; it instructs consumers to match on the PREFIX. Three
 * shapes are emitted today, and a fourth arriving in a later minor must not turn into a refusal --
 * an informational descriptor is echoed opaquely (spec/compatibility.md section 3), never failed
 * closed.
 */
enum class EMantlePlaceDrapeAlignment : uint8
{
	/** The manifest published no `alignment` at all. It is optional; absence is not a defect. */
	Absent,
	/** "matches heightmap AOI-UTM extent" -- the imagery spans the heightmap's own extent. */
	Matches,
	/** "unverified - heightmap extent unavailable" -- the ETL had nothing to compare against. */
	Unverified,
	/** "diverges from heightmap extent (imagery covers <pct> ..., overshoot <ratio>x)". */
	Diverges,
	/** A shape this plugin does not know. Surfaced verbatim and imported; never a refusal. */
	Unrecognised,
};

/**
 * The parsed descriptor: the shape, the string verbatim (HPS-36 -- a value a user reads is echoed,
 * never re-worded), and the measured numbers the divergent shape carries.
 */
struct FMantlePlaceDrapeAlignment
{
	EMantlePlaceDrapeAlignment Shape = EMantlePlaceDrapeAlignment::Absent;

	/** Exactly what the manifest said. Empty only when the field is absent. */
	FString Declared;

	/** True when BOTH measured figures were readable out of the divergent shape. */
	bool bHasMeasured = false;

	/** The percentage of the heightmap the imagery covers, as the ETL measured it. */
	double CoveredPercent = 0.0;

	/** How far the imagery overshoots the heightmap, as the ETL measured it (a ratio, not a %). */
	double OvershootRatio = 0.0;

	/** True when this shape is one the importer must warn a user about before they go looking for
	 *  the misalignment themselves. Only the divergent shape earns a warning. */
	bool ShouldWarn() const { return Shape == EMantlePlaceDrapeAlignment::Diverges; }
};

/**
 * Pure (engine-/IO-free) logic for `hosts.unreal.imagery_drape.alignment`: classify the descriptor
 * by prefix, read the numbers out of the divergent shape, and check the claim against the two
 * rectangles the manifest publishes for itself.
 *
 * Nothing here derives a placement value. The UV transform still comes from the declared extents
 * verbatim (spec/format.md section 6); this unit only compares two published rectangles against a
 * published claim about them, which is the same verification the importer's coverage warning has
 * always performed.
 */
struct FMantlePlaceDrapeAlignmentLogic
{
	/** The relative slack allowed between the drape rectangle and the AOI rectangle before the two
	 *  are called different. 1% -- the same threshold the importer's coverage warning uses, kept as
	 *  one constant so the refusal and the warning cannot drift apart. The reference bundle's
	 *  imagery is ~0.1% short of its AOI and truthfully declares "matches", which is exactly the
	 *  margin this slack exists to leave alone. */
	static constexpr double ExtentSlack = 0.01;

	/** Classify `Declared` by prefix and read the divergent shape's measured figures. */
	static FMantlePlaceDrapeAlignment Classify(const FString& Declared);

	/**
	 * The manifest's alignment claim against the manifest's own rectangles.
	 *
	 * Fails closed (false + OutError) for ONE case: a bundle that declares the imagery matches the
	 * heightmap extent while its own drape rectangle and AOI rectangle disagree by more than
	 * ExtentSlack. That bundle imports looking correct and is silently mis-draped, and the two
	 * numbers that prove it are both in the manifest.
	 *
	 * Everything else proceeds. A divergent claim is believed rather than re-adjudicated -- the UV
	 * transform already rides the declared extents, so the import is placed correctly and the user
	 * only needs telling. An unverified or unrecognised shape says nothing to contradict.
	 *
	 * Needs the heightmap: with no AOI rectangle there is nothing to compare, and a bundle that
	 * ships a drape and no heightmap is a legitimate mesh-only import.
	 */
	static bool CheckAgainstExtents(
	    const FMantlePlaceVaultManifest& Manifest,
	    const FMantlePlaceDrapeAlignment& Alignment,
	    FString& OutError);

	/**
	 * The line the importer logs for this bundle's drape, or empty when the descriptor is absent.
	 * A divergent shape produces a WARNING carrying the measured numbers; the other shapes produce
	 * a plain note, because "the ETL could not check" is not something a user must act on.
	 */
	static FString DescribeForLog(const FMantlePlaceDrapeAlignment& Alignment);
};
