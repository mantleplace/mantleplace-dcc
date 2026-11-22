// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

using UnrealBuildTool;

public class MantlePlaceRuntime : ModuleRules
{
	public MantlePlaceRuntime(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;
		
		PublicIncludePaths.AddRange(
			new string[] {
			}
			);
				
		
		PrivateIncludePaths.AddRange(
			new string[] {
			}
			);
			
		
		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core",
				"CoreUObject",
				"Engine",
				"PCG",
			}
			);


		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"Slate",
				"SlateCore",
				"HTTP",
				"Json",
				"HTTPServer",
				"Sockets",
				"Projects",
			}
			);

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PublicSystemLibraries.Add("Crypt32.lib");
			PublicSystemLibraries.Add("Bcrypt.lib");
		}


		DynamicallyLoadedModuleNames.AddRange(
			new string[]
			{
			}
			);
	}
}
