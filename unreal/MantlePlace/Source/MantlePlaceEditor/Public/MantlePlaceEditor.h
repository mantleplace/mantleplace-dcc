// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "Modules/ModuleManager.h"

class SDockTab;
class FSpawnTabArgs;

class FMantlePlaceEditorModule : public IModuleInterface
{
public:
	/** IModuleInterface implementation */
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

private:
	/** Spawns the dockable Mantle Place vault panel for the Window-menu nomad tab. */
	TSharedRef<SDockTab> SpawnVaultTab(const FSpawnTabArgs& Args);
};
