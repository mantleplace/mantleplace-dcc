// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

struct FMantlePlaceVaultItem;

/**
 * The decision behind a scripted import at a named level (`StartVaultImportAtLevel`): whether the order
 * can be downloaded at the level the script named. Pure, so every refusal and every word is asserted
 * headless; the orchestrator calls it before any request and downloads through the same path the import
 * window's Download button takes.
 *
 *  - **The levels are the order's own offer** (the vault listing's `Levels`), applied verbatim: this unit
 *    knows no level of its own (HPS-33).
 *  - **A refusal says why** and what to do instead, in words a script's log can show.
 *  - **Nothing here picks a level.** An empty level is refused, not defaulted: taking the platform's
 *    recommendation is `StartVaultImport`'s, which a script calls when it does not care.
 */
class FMantlePlaceScriptedLevelLogic
{
public:
	/**
	 * Why Item cannot be downloaded at Level, or empty when it can. Case-sensitive, as the import window's
	 * choice is: `Min` is a level and `min` is not.
	 */
	static FString Refusal(const FMantlePlaceVaultItem& Item, const FString& Level);
};
