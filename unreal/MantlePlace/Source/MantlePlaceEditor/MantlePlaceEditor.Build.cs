// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

using UnrealBuildTool;

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
				"DataLayerEditor",
				"DeveloperSettings",
				"Engine",
				"MantlePlaceRuntime",
				"UnrealEd",
				"EditorSubsystem",
				"AssetTools",
				"AssetRegistry",
				"Landscape",
				"PCG",
				"RHI",
				"RenderCore",
				"FileUtilities",
				"ImageWrapper",
				"Json",
				"SQLiteCore",
				"HTTP",
				"HTTPServer",
				"DesktopPlatform",
				"Slate",
				"SlateCore",
				"InputCore",
				"WorkspaceMenuStructure",
				"ToolMenus",
				"Projects",
			}
		);
	}
}
