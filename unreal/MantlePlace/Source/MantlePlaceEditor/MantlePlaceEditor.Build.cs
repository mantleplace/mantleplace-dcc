// Copyright Mantle Place. All Rights Reserved.

using UnrealBuildTool;

// Editor-only module: the local-zip vault-package importer (heightmap -> Landscape,
// Terrain.glb -> StaticMesh, aerial imagery draped on top). Never cooked into a build.
public class MantlePlaceEditor : ModuleRules
{
	public MantlePlaceEditor(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core",
			}
		);

		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"CoreUObject",
				"DeveloperSettings", // UDeveloperSettings: the generated-content-root project setting
				"Engine",
				"MantlePlaceRuntime", // auth base + vault client + bundle cache the vault-import orchestrator drives
				"UnrealEd",      // FScopedTransaction, GEditor, factories, FActorLabelUtilities
				"EditorSubsystem", // UEditorSubsystem: UMantlePlaceAuthSubsystem holds the editor's one auth session
				"AssetTools",    // FAssetToolsModule: import tasks + CreateAsset
				"AssetRegistry", // FAssetRegistryModule::AssetCreated for the landscape layer-info assets
				"Landscape",     // ALandscapeProxy::Import, ULandscapeInfo, ULandscapeSubsystem
				"RenderCore",    // FlushRenderingCommands (finalize the landscape drape render state)
				"FileUtilities", // FZipArchiveReader (editor-only, libzip-backed)
				"ImageWrapper",  // 16-bit grayscale PNG decode
				"Json",          // Metadata/manifest.json parse
				"HTTPServer",    // FMantlePlaceLocalTileServer: stream the bundle to Cesium for Unreal
				"DesktopPlatform", // IDesktopPlatform::OpenFileDialog (Browse button)
				"Slate",         // SMantlePlaceVaultPanel/Row: the native vault tooling UI
				"SlateCore",     // Slate widget + style (FAppStyle) types
				"InputCore",     // EKeys::* referenced by the SListView/SComboBox/STableRow templates
				"WorkspaceMenuStructure", // GetLevelEditorCategory() -> the Window-menu tab entry
				"Projects",      // IPluginManager: locate the plugin Resources dir for the tab-icon style set
			}
		);
	}
}
