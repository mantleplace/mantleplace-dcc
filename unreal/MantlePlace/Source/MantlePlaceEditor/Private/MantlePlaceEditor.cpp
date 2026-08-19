// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceEditor.h"

#include "Framework/Application/SlateApplication.h"
#include "Framework/Docking/TabManager.h"
#include "MantlePlaceEditorStyle.h"
#include "Slate/SMantlePlaceVaultPanel.h"
#include "Widgets/Docking/SDockTab.h"
#include "WorkspaceMenuStructure.h"
#include "WorkspaceMenuStructureModule.h"

#define LOCTEXT_NAMESPACE "FMantlePlaceEditorModule"

namespace
{
	/** Global-tab-manager id for the Mantle Place vault panel. */
	const FName MantlePlaceVaultTabId(TEXT("MantlePlaceVault"));
}

void FMantlePlaceEditorModule::StartupModule()
{
	// No editor UI in a cook/commandlet/-nullrhi run; the tab manager isn't meaningful there.
	if (IsRunningCommandlet() || !FSlateApplication::IsInitialized())
	{
		return;
	}

	// Register the style set (the Mantle Place logo tab icon) before wiring the tab that references it.
	FMantlePlaceEditorStyle::Initialize();

	// Register a nomad tab in the Level-Editor workspace category. As with Cesium, that SetGroup call
	// is what surfaces the entry under Window > Mantle Place; no ToolMenus / command wiring is needed.
	FGlobalTabmanager::Get()
		->RegisterNomadTabSpawner(
			MantlePlaceVaultTabId,
			FOnSpawnTab::CreateRaw(this, &FMantlePlaceEditorModule::SpawnVaultTab))
		.SetGroup(WorkspaceMenu::GetMenuStructure().GetLevelEditorCategory())
		.SetDisplayName(LOCTEXT("MantlePlaceTabTitle", "Mantle Place"))
		.SetTooltipText(LOCTEXT("MantlePlaceTabTooltip", "Open the Mantle Place vault panel: browse & import owned bundles, or import a local .zip."))
		.SetIcon(FSlateIcon(FMantlePlaceEditorStyle::GetStyleSetName(), "MantlePlace.TabIcon"));
}

void FMantlePlaceEditorModule::ShutdownModule()
{
	if (FSlateApplication::IsInitialized())
	{
		FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(MantlePlaceVaultTabId);
	}
	FMantlePlaceEditorStyle::Shutdown();
}

TSharedRef<SDockTab> FMantlePlaceEditorModule::SpawnVaultTab(const FSpawnTabArgs& Args)
{
	return SNew(SDockTab)
		.TabRole(ETabRole::NomadTab)
		[
			SNew(SMantlePlaceVaultPanel)
		];
}

#undef LOCTEXT_NAMESPACE

IMPLEMENT_MODULE(FMantlePlaceEditorModule, MantlePlaceEditor)
