// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * The layer family an artifact's actors belong to: one World Partition Data Layer per family, so a
 * user toggles "buildings" or "roads" natively in the engine rather than choosing them once at
 * import. Each artifact handler declares its family (IMantlePlaceArtifactHandler::LayerFamily); the
 * families are what the importer already knows from its own handlers, never derived from data the
 * manifest does not publish.
 *
 * None is the family of an artifact that spawns no actor of its own (it is still declared, so a
 * handler cannot forget to decide).
 */
enum class EMantlePlaceLayerFamily : uint8
{
	None,
	Terrain,
	Coverage,
	Buildings,
	Roads,
	Foliage,
	PublicRecords,
};

/**
 * The decisions behind Data Layer assignment, as pure functions. MantlePlaceDataLayers executes
 * them; MantlePlace.Import.DataLayer.Logic asserts them.
 *
 * Scoping, decided: a Data Layer is per FAMILY and shared across every import in the project, not
 * per order. Two imported areas then toggle together (hiding "Buildings" hides every import's
 * buildings), the Data Layers outliner holds one row per family however many orders are imported,
 * and a wipe never deletes a layer another import still uses. The asset therefore lives outside
 * every order's folder (MantlePlaceImportNaming::DataLayerPackageName), where no re-import's
 * force-delete can reach it.
 */
namespace MantlePlaceDataLayerLogic
{
/** Every family an actor can be assigned to, in a fixed order. None is not one of them. */
TArray<EMantlePlaceLayerFamily> AssignableFamilies();

/** The family's name as the Data Layer asset and the outliner show it; empty for None. */
FString FamilyName(EMantlePlaceLayerFamily Family);

/**
 * Whether an actor claimed for this family is assigned to a Data Layer: only in a World Partition
 * level (a non-partitioned level has no Data Layers, and its import is unchanged), and only for a
 * real family.
 */
bool ShouldAssign(bool bPartitioned, EMantlePlaceLayerFamily Family);
}
