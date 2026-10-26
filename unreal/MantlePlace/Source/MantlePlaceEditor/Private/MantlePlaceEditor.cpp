// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceEditor.h"

#include "Framework/Application/SlateApplication.h"
#include "Framework/Docking/TabManager.h"
#include "MantlePlaceEditorStyle.h"
#include "MantlePlaceHandover.h"
#include "Slate/SMantlePlaceVaultPanel.h"
#include "Framework/Notifications/NotificationManager.h"
#include "ToolMenus.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/Notifications/SNotificationList.h"
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
		.SetTooltipText(LOCTEXT("MantlePlaceTabTooltip", "Open the Mantle Place vault panel: browse & import bundles you keep, or import a local .zip."))
		.SetIcon(FSlateIcon(FMantlePlaceEditorStyle::GetStyleSetName(), "MantlePlace.TabIcon"));

	// The handover command, in the Tools menu once the menus exist.
	UToolMenus::RegisterStartupCallback(FSimpleMulticastDelegate::FDelegate::CreateRaw(this, &FMantlePlaceEditorModule::RegisterMenus));
}

void FMantlePlaceEditorModule::RegisterMenus()
{
	FToolMenuOwnerScoped OwnerScoped(this);
	UToolMenu* Menu = UToolMenus::Get()->ExtendMenu("LevelEditor.MainMenu.Tools");
	FToolMenuSection& Section = Menu->FindOrAddSection("MantlePlace", LOCTEXT("MantlePlaceMenuSection", "Mantle Place"));
	Section.AddMenuEntry(
		"MantlePlacePrepareForHandover",
		LOCTEXT("PrepareForHandoverLabel", "Prepare for handover"),
		LOCTEXT("PrepareForHandoverTooltip", "Remove the ODbL content the import filed under ODbL (or swap in its Produced Work) and add the credit, so this level is safe to ship or share. One undo step; nothing is saved."),
		FSlateIcon(),
		FUIAction(FExecuteAction::CreateLambda([]()
		{
			FNotificationInfo Info(FText::FromString(MantlePlaceHandover::PrepareEditorWorld().Summary()));
			Info.ExpireDuration = 12.0f;
			FSlateNotificationManager::Get().AddNotification(Info);
		})));
}

void FMantlePlaceEditorModule::ShutdownModule()
{
	if (UObjectInitialized())
	{
		UToolMenus::UnRegisterStartupCallback(this);
		UToolMenus::UnregisterOwner(this);
	}
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
