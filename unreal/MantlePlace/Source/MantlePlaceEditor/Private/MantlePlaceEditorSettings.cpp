// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceEditorSettings.h"

#include "MantlePlaceImportNaming.h"

#include "UObject/UObjectGlobals.h"  // GetDefault

UMantlePlaceEditorSettings::UMantlePlaceEditorSettings()
	: GeneratedContentRoot(MantlePlaceImportNaming::DefaultContentRoot())
{
}

FString UMantlePlaceEditorSettings::ResolveGeneratedContentRoot()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	// GetDefault on a UDeveloperSettings does not return null in a running editor, but the importer
	// is also driven from automation and from Blueprint, so the fallback is stated rather than
	// assumed. Validation lives in the naming module, where it is asserted headlessly.
	return MantlePlaceImportNaming::ResolveContentRoot(
		Settings != nullptr ? Settings->GeneratedContentRoot : FString());
}

FName UMantlePlaceEditorSettings::GetCategoryName() const
{
	// Under Plugins, not Project: this configures a plugin, and a reader looking for it will look
	// where the other plugins are.
	return TEXT("Plugins");
}
