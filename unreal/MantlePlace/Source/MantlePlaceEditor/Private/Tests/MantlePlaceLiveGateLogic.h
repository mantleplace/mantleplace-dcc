// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;

/**
 * Running statistics of landscape-minus-published-ground heights (dz), in whatever unit the caller adds
 * them in. The live import test's placement gate accumulates one per check point it can measure.
 */
struct FMantlePlaceDzStatistics
{
	int32 Count = 0;
	double Sum = 0.0;
	double SumSquares = 0.0;
	double MaxAbs = 0.0;

	void Add(double Dz);

	/** 0.0 when nothing was added, so a caller checks `Count` first. */
	double Mean() const;
	double Rms() const;
};

/**
 * Pure (engine-/IO-free) decisions behind the live import test (`MantlePlace.Import.LiveFreeTier`): its
 * placement gate and its drape smoke check.
 * This is test support: it lives with the tests and is read by nothing the plugin ships. It derives no
 * placement value -- it converts a unit the manifest states, judges a landscape against the bundle's own
 * published check points, and asks the importer's own decisions (a drape placed or skipped) rather than
 * re-deriving them.
 */
struct FMantlePlaceLiveGateLogic
{
	/**
	 * The largest systematic offset (|mean dz|) the placement gate accepts, in centimetres. A landscape
	 * placed a datum, a post or a flip off carries an offset in the mean; terrain noise averages out of it,
	 * so this is tight. The five published DEM cells of a real sample agree with the landscape to 1.5 cm.
	 */
	static constexpr double MaxMeanOffsetCm = 10.0;

	/**
	 * The largest spread (RMS dz) the placement gate accepts, in centimetres. `SurfacePoints.csv` is a
	 * decimated point set, roughly 4 m apart, not a copy of the DEM's cells (~1.2 m posts), so a single
	 * point can sit several metres off on a steep cell with no placement fault at all. A bound on every
	 * point's error would fail a correct import; the RMS stays well under a metre on one, and a misplaced
	 * landscape does not fit under it.
	 */
	static constexpr double MaxRmsCm = 100.0;

	/**
	 * Metres per unit of `elevation.points_csv.units`, compared verbatim: `m` 1, `ftUS` 1200/3937 (the US
	 * survey foot, exact), `ft` 0.3048 (the international foot, exact). An empty string is an absent field,
	 * which means `m`. Any other unit returns false with the reason, and the gate does not run: a unit this
	 * test cannot convert is never guessed.
	 */
	static bool TryUnitToMetres(const FString& Units, double& OutMetresPerUnit, FString& OutReason);

	/** |mean| within `MaxMeanOffsetCm`. */
	static bool MeanWithinTolerance(const FMantlePlaceDzStatistics& Stats);

	/** RMS within `MaxRmsCm`. */
	static bool RmsWithinTolerance(const FMantlePlaceDzStatistics& Stats);

	/**
	 * The line the import reports for an imagery drape it skipped ("Skipped Imagery drape: <reason>"), asked
	 * of the importer's own skip report so it is never re-worded here; empty when the manifest names no skip
	 * for the drape (a drape placed, or a 2.x bundle with none).
	 */
	static FString DrapeSkipLine(const FMantlePlaceVaultManifest& Manifest);

	/**
	 * The drape smoke check: empty when the import did what the manifest says, else why it did not.
	 *
	 *  - A drape the import placed (`Manifest.Drape.bPresent`) must have reached the landscape's render
	 *    material: `bMaterialIsDrape` is false when the component was left on the engine default surface.
	 *  - A drape the import skipped, as it must for imagery that states no rectangle (HPS-53), leaves the
	 *    default surface by design, so no material is asserted; the import log (`ImportMessage`) must name
	 *    the skip instead, with the importer's own line.
	 *  - A manifest that declares no drape at all and names no skip must still say the landscape is
	 *    geometry only.
	 */
	static FString JudgeDrape(const FMantlePlaceVaultManifest& Manifest, const FString& ImportMessage, bool bMaterialIsDrape);
};
