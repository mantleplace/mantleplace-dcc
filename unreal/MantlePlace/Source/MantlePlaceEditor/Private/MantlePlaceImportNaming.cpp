// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportNaming.h"

#include "AssetToolsModule.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "IAssetTools.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Materials/MaterialInterface.h"
#include "Misc/PackageName.h"
#include "Modules/ModuleManager.h"
#include "UObject/Package.h"

namespace MantlePlaceImportNaming
{
	static const TCHAR* PrefixForClass(const UClass* Class)
	{
		if (Class == nullptr)
		{
			return nullptr;
		}
		// MaterialInstanceConstant is a UMaterialInterface, so test it before the base class.
		if (Class->IsChildOf(UStaticMesh::StaticClass())) { return TEXT("SM_"); }
		if (Class->IsChildOf(UMaterialInstanceConstant::StaticClass())) { return TEXT("MI_"); }
		if (Class->IsChildOf(UMaterialInterface::StaticClass())) { return TEXT("M_"); }
		if (Class->IsChildOf(UTexture::StaticClass())) { return TEXT("T_"); }
		return nullptr;
	}

	void RenameToConvention(UObject* Asset)
	{
		if (Asset == nullptr)
		{
			return;
		}
		const TCHAR* Prefix = PrefixForClass(Asset->GetClass());
		if (Prefix == nullptr)
		{
			return;
		}
		const FString Name = Asset->GetName();
		if (Name.StartsWith(Prefix))
		{
			return;
		}

		const FString PackagePath = FPackageName::GetLongPackagePath(Asset->GetPackage()->GetName());
		const FString NewName = FString(Prefix) + Name;

		FAssetToolsModule& Module = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools"));
		TArray<FAssetRenameData> Renames;
		Renames.Emplace(Asset, PackagePath, NewName);
		Module.Get().RenameAssets(Renames);
	}
}
