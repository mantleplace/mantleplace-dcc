// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultManifest;
class AActor;
class UTexture2D;
class UMaterialInstanceConstant;

namespace MantlePlaceDrape
{
	/** Import Imagery.png (already extracted to disk) as a saveable Texture2D asset. */
	UTexture2D* ImportTexture(const FString& ImageryFile, const FString& DestPackagePath, FString& OutError);

	/**
	 * Create a saveable Material Instance Constant from the drape template, bound to the
	 * imagery texture and the world-space extent so the material maps it onto its true
	 * geographic footprint. Returns nullptr (with OutError) if the template is missing.
	 */
	UMaterialInstanceConstant* CreateDrapeMaterial(
		const FMantlePlaceVaultManifest& Manifest,
		UTexture2D* Texture,
		const FString& DestPackagePath,
		FString& OutError);

	/**
	 * Assign the drape MIC to a Landscape (LandscapeMaterial) or a StaticMeshActor (slot 0).
	 *
	 * Returns false, having changed nothing and logged why, for any target it cannot drape: a null
	 * argument, an actor of a type it does not know, or -- the case that matters -- a landscape
	 * whose `LandscapeMaterial` property this engine build does not expose to reflection. Fail
	 * closed: an unassigned drape is a visible, reportable miss, and it is the lesser of the two
	 * outcomes available here.
	 */
	bool AssignMaterial(AActor* Target, UMaterialInstanceConstant* Mic);

	/**
	 * The `ALandscapeProxy` property `AssignMaterial` drives by name, because the engine does not
	 * export the setter that would drive it for us.
	 *
	 * Named here rather than spelled at the lookup so the tripwire below and the assignment can
	 * never disagree about which property is meant.
	 */
	extern const TCHAR* const LandscapeMaterialPropertyName;

	/**
	 * Whether this engine build still exposes that property -- the engine-version tripwire.
	 *
	 * `AssignMaterial` reaches the landscape material through reflection, so an engine rename is
	 * invisible to the compiler and used to be a hard `check` in a user's session. This is the same
	 * question asked somewhere it can be answered out loud: `MantlePlace.Import.DrapeEngineTripwire`
	 * asserts it, so a rename turns the automation suite red instead of turning up as a crash.
	 * Verified against UE 5.8, the engine version this plugin targets.
	 */
	bool IsLandscapeMaterialPropertyResolvable();
}
