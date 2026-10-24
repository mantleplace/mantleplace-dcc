// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * What a bundle states about one file's ODbL duty (`file_entries[<path>].odbl_role`, MPB 1.14.0).
 *
 * Database is the ODbL Derivative Database, licensed ODbL; ProducedWork is rasterized, meshed,
 * extruded or baked output, which is not; None means no ODbL source contributes.
 */
enum class EMantlePlaceOdblRole : uint8
{
	Database,
	ProducedWork,
	None,
};

/** One upstream source of a file (`file_entries[<path>].sources[]`): its name and its single licence id. */
struct FMantlePlaceFileSource
{
	FString Name;
	FString License;
};

/** What `file_entries[<path>]` says about a file's terms. The frame members are not read here (HPS-33). */
struct FMantlePlaceFileLicensing
{
	EMantlePlaceOdblRole Role = EMantlePlaceOdblRole::Database;
	FString License;                       // the SPDX expression, verbatim; empty when absent
	TArray<FMantlePlaceFileSource> Sources;
};

/** One `attribution.sources[]` entry: the manifest's own credit words, applied verbatim. */
struct FMantlePlaceAttributionSource
{
	FString ProviderId;
	FString AttributionText;
	FString License;
	FString LicenseUrl;
};

/**
 * The ODbL decisions, as pure functions: which imported content is a Derivative Database, what the
 * credit says, and what Prepare for handover does to each piece. MantlePlaceHandover executes them;
 * MantlePlace.Import.Odbl.Logic asserts them.
 *
 * This plugin makes no licensing decision of its own. It reads the role the manifest states and
 * fails closed the way spec/format.md section 6.8 says: a missing role reads as `database`, and so
 * does a role this reader does not know.
 */
namespace MantlePlaceOdblLogic
{
/** The role a stated value names. `bStated` false (no member) and any unrecognised value read as Database. */
EMantlePlaceOdblRole ParseRole(bool bStated, const FString& Value);

/** The role's published word: `database`, `produced-work` or `none`. */
FString RoleName(EMantlePlaceOdblRole Role);

/**
 * The `file_entries` entry for one archive path, or null when the manifest states none. The exact key
 * first, then any key that differs only by case: a reader matches a path regardless of case (MPB 2.0).
 */
const FMantlePlaceFileLicensing* FindEntry(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path);

/** The role of one file. A file with no `file_entries` entry has no stated role, so it is Database. */
EMantlePlaceOdblRole RoleOfPath(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const FString& Path);

/**
 * The role of everything imported from these files together: Database when any of them is, else
 * ProducedWork when any of them is, else None. No files at all (an artifact imported from nothing the
 * bundle ships as a file) is None: there is no file whose role could be missing.
 */
EMantlePlaceOdblRole RoleOfPaths(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths);

/**
 * Whether content imported from these files needs the credit: a Database, or anything made from a
 * source whose licence is ODbL (a Produced Work still owes the notice).
 */
bool NeedsCredit(const TMap<FString, FMantlePlaceFileLicensing>& Entries, const TArray<FString>& Paths);

/** One credit line for an attribution source: the manifest's words, joined, empty fields dropped. */
FString CreditLine(const FMantlePlaceAttributionSource& Source);

/**
 * The credit lines for content imported from these files: the manifest's `attribution.sources[]`
 * when it states any, and otherwise the name and licence of the ODbL sources of these files. Deduped,
 * in order. Empty when the manifest gives nothing to credit.
 */
TArray<FString> CreditLines(
	const TArray<FMantlePlaceAttributionSource>& Attribution,
	const TMap<FString, FMantlePlaceFileLicensing>& Entries,
	const TArray<FString>& Paths);

// --- Prepare for handover ---------------------------------------------------------------------

/** One imported actor, as the handover sees it (read back from the tags the import wrote). */
struct FHandoverItem
{
	FString Identity;                  // the order the actor belongs to
	bool bOdbl = false;                // imported from `database` files
	FString EquivalenceGroup;          // artifacts that stand for the same thing (terrain); empty = none
	TArray<FString> CreditLines;       // the credit this actor's import recorded; empty = none owed
};

enum class EHandoverDecision : uint8
{
	/** Not ODbL content: left exactly as it is. */
	Keep,
	/** ODbL content with no Produced Work equivalent in the level: removed. */
	Remove,
	/** ODbL content whose equivalence group holds a Produced Work in the same order: removed, that stays. */
	Swap,
};

struct FHandoverCredit
{
	FString Identity;
	TArray<FString> Lines;
};

struct FHandoverPlan
{
	TArray<EHandoverDecision> Decisions;   // parallel to the items
	TArray<FHandoverCredit> Credits;       // one per order that owes one, in first-seen order
};

/**
 * What to do with each item. An ODbL item is Swap when another item of the SAME order and the same
 * non-empty equivalence group is not ODbL (the Produced Work already in the level stands in for it),
 * and Remove otherwise. A credit is owed by every order that has any item with credit lines, whether
 * or not its ODbL content was removed: a Produced Work from an ODbL source still carries the notice.
 */
FHandoverPlan PlanHandover(const TArray<FHandoverItem>& Items);

/** The text a credit actor carries: the lines, one per line. */
FString CreditText(const TArray<FString>& Lines);
}
