// Copyright Mantle Place. All Rights Reserved.

using UnrealBuildTool;

public class MantlePlaceRuntime : ModuleRules
{
	public MantlePlaceRuntime(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;
		
		PublicIncludePaths.AddRange(
			new string[] {
				// ... add public include paths required here ...
			}
			);
				
		
		PrivateIncludePaths.AddRange(
			new string[] {
				// ... add other private include paths required here ...
			}
			);
			
		
		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core",
				"CoreUObject", // USTRUCT row types in public headers (MantlePlaceLandcoverTypes.h)
				"Engine",      // FTableRowBase in MantlePlaceLandcoverTypes.h (tree-points DataTable rows)
			}
			);


		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"Slate",
				"SlateCore",
				"HTTP",        // Mantle Place platform API (Supabase GoTrue) over HTTP
				"Json",        // request body construction + auth response parsing
				"HTTPServer",  // loopback (127.0.0.1) OAuth redirect callback server (RFC 8252)
				"Projects",    // IPluginManager: the conformance-corpus reader anchors its walk on the plugin dir
				// ... add private dependencies that you statically link with here ...
			}
			);

		// Windows crypto: DPAPI (Crypt32) encrypts the token store; CNG (Bcrypt) provides the
		// BCryptGenRandom CSPRNG for the PKCE code_verifier + state.
		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PublicSystemLibraries.Add("Crypt32.lib");
			PublicSystemLibraries.Add("Bcrypt.lib");
		}


		DynamicallyLoadedModuleNames.AddRange(
			new string[]
			{
				// ... add any modules that your module loads dynamically here ...
			}
			);
	}
}
