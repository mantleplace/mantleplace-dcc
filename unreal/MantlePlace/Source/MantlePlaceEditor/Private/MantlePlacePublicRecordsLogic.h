// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceManifestBlocks.h"

/**
 * One piece of DXF linework: a polyline (or a LINE as two points) in the file's own coordinates,
 * easting then northing. Nothing is converted on the way in -- whether the numbers are in this
 * host's frame is decided before a file is read (CanPlaceFrame) and never repaired after.
 */
struct FMantlePlaceRecordPolyline
{
	TArray<FVector2D> Points; // X = easting, Y = northing, as the file states them
	bool bClosed = false;
};

/**
 * Pure (engine-/IO-free) logic for the public-record layers: which families a manifest names and in
 * what order, whether a file's stated frame is this host's, the DXF linework reader, and the words
 * the user reads (credit, disclaimer, notice, and the records a bundle does not deliver). The layers
 * are the public record passed through unchanged: this unit reads and describes them, derives no
 * value from them, and never edits a record (the thin-client boundary, HPS-33). Deterministic and
 * headless-testable; the importer shim owns the impure parts (zip read, actor spawning).
 */
struct FMantlePlacePublicRecordsLogic
{
	/** True for one of the ten families the published layout names. Any other family still imports. */
	static bool IsKnownFamily(const FString& Family);

	/**
	 * The family's name as the user reads it: "Parcels", "Flood zones", "Protected areas"; a family
	 * this reader has not met is its own key with `_` and `-` read as spaces and each word capitalised.
	 */
	static FString DisplayName(const FString& Family);

	/**
	 * The known families in the published order (parcels, zoning, flood, wetlands, soils, protected,
	 * hydrography, habitat, contamination, historic), then any other family by key. A stable order that
	 * does not depend on the order the manifest's members happened to be read in.
	 */
	static void OrderLayers(TArray<FMantlePlaceRecordLayer>& Layers);

	/**
	 * Whether the file's stated frame is this host's (HPS-53). A file is shown only where the format
	 * states its frame for it: the type `projected`, a CRS equal to `HostCrs` (compared verbatim) and
	 * a horizontal unit of `m`. A frame the entry does not state, states in part, or states as anything
	 * else is unplaceable, with OutReason saying which; an unstated CRS or unit is never assumed to
	 * match. The vertical unit is not asked: no height from a record file is used, the overlay is laid
	 * on the imported ground. Never a conversion.
	 */
	static bool CanPlaceFrame(const FMantlePlaceRecordFrame& Frame, const FString& HostCrs, FString& OutReason);

	/**
	 * Read ASCII DXF linework. LWPOLYLINE, POLYLINE (with its VERTEX records, until SEQEND; a polyface
	 * or 3D mesh is skipped) and LINE become polylines; every other entity is counted in
	 * OutEntitiesSkipped and left out. Fails closed (false + OutError) on a binary DXF, on text that
	 * is not group-code/value pairs, and on a drawing with no ENTITIES section; a drawing whose
	 * ENTITIES hold none of the three kinds is a success with no lines, which the caller reports.
	 */
	static bool ParseDxf(
	    const FString& Text,
	    TArray<FMantlePlaceRecordPolyline>& OutLines,
	    int32& OutEntitiesSkipped,
	    FString& OutError);

	/**
	 * The backstop HPS-53 keeps behind a stated frame: a producer can state a frame wrongly, so a file
	 * none of whose vertices lands inside the landscape's published span is not in this frame and is
	 * refused on that evidence. It runs one way -- a vertex inside proves nothing and places nothing.
	 * `SpanUeCm` is FMantlePlaceVaultManifest::GetAoiSizeUeCm() (X = North, Y = East, centred on the
	 * origin); a zero span is no evidence and returns true. A record layer is clipped to the order's
	 * area, which reaches past the landscape's edge, so one vertex inside is enough to keep the file.
	 */
	static bool AnyVertexOnSite(
	    const TArray<FMantlePlaceRecordPolyline>& Lines,
	    double OriginEastingM,
	    double OriginNorthingM,
	    const FVector2D& SpanUeCm);

	// --- What the user reads ----------------------------------------------------------------------

	/**
	 * The one sentence every import of a public-record layer carries: passed through from the record
	 * holder unchanged, not a survey, not a legal determination. Shown whether or not the manifest
	 * carries its own notice for a layer.
	 */
	static FString StandingNotice();

	/** The credit line for a layer, then its disclaimer and notice when the manifest carries them. */
	static TArray<FString> DescribeLayer(const FMantlePlaceRecordLayer& Layer);

	/** A record pointer, as a sentence naming the holder and where it publishes the record. */
	static FString DescribePointer(const FString& Family, const FMantlePlaceRecordPointer& Pointer);

	/** A source that covers the extent and delivered nothing, as a sentence. */
	static FString DescribeSkippedSource(const FMantlePlaceSkippedSource& Source);

	/** Every record the bundle does not deliver: each pointer, then each skipped source. Empty when none. */
	static TArray<FString> DescribeNotDelivered(const FMantlePlacePublicRecordsBlock& Block);
};
