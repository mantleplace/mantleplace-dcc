// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceImportNaming.h"

#include "AssetToolsModule.h"
#include "Engine/DataTable.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "IAssetTools.h"
#include "LandscapeLayerInfoObject.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Materials/MaterialInterface.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "UObject/Package.h"

namespace MantlePlaceImportNaming
{
	namespace
	{
		// The asset-name prefixes, as documented in `unreal/CLAUDE.md`. Ordered most-derived first:
		// MaterialInstanceConstant is a UMaterialInterface, so it has to be tested before the base
		// class or every instance would come out `M_`.
		const TCHAR* PrefixForClass(const UClass* Class)
		{
			if (Class == nullptr)
			{
				return nullptr;
			}
			if (Class->IsChildOf(UStaticMesh::StaticClass())) { return TEXT("SM_"); }
			if (Class->IsChildOf(UMaterialInstanceConstant::StaticClass())) { return TEXT("MI_"); }
			if (Class->IsChildOf(UMaterialInterface::StaticClass())) { return TEXT("M_"); }
			if (Class->IsChildOf(UTexture::StaticClass())) { return TEXT("T_"); }
			if (Class->IsChildOf(ULandscapeLayerInfoObject::StaticClass())) { return TEXT("LI_"); }
			if (Class->IsChildOf(UDataTable::StaticClass())) { return TEXT("DT_"); }
			return nullptr;
		}

		const TCHAR* SubfolderName(ESubfolder Subfolder)
		{
			switch (Subfolder)
			{
			case ESubfolder::Imagery:         return TEXT("Imagery");
			case ESubfolder::Mesh:            return TEXT("Mesh");
			case ESubfolder::Buildings:       return TEXT("Buildings");
			case ESubfolder::CoverageRasters: return TEXT("CoverageRasters");
			case ESubfolder::Landcover:       return TEXT("Landcover");
			}
			// Unreachable for a valid enumerator. Returning a name rather than an empty string keeps
			// a future enumerator someone forgot to list out of the import root itself, where the
			// re-import wipe would then reach the whole import instead of one subfolder.
			checkNoEntry();
			return TEXT("Unclassified");
		}

		const TCHAR* ActorKindName(EActorKind Kind)
		{
			switch (Kind)
			{
			case EActorKind::Landscape: return TEXT("Landscape");
			case EActorKind::Mesh:      return TEXT("Mesh");
			case EActorKind::Buildings: return TEXT("Buildings");
			}
			checkNoEntry();
			return TEXT("Actor");
		}

		// The abbreviation permitted on outliner-visible labels, and only there. See ADR 0003.
		const TCHAR* const ActorLabelPrefix = TEXT("MP_");
	}

	FString ShortIdentity(const FString& Identity)
	{
		return Identity.Left(8);
	}

	const TCHAR* DefaultContentRoot()
	{
		return TEXT("/Game/MantlePlace");
	}

	FString ImportRoot(const FString& ContentRoot, const FString& Identity)
	{
		return ContentRoot / ShortIdentity(Identity);
	}

	FString SubfolderPath(const FString& ImportRootPath, ESubfolder Subfolder)
	{
		return ImportRootPath / SubfolderName(Subfolder);
	}

	FString ObjectPathIn(const FString& PackagePath, const FString& AssetName)
	{
		return ObjectPathOf(PackagePath / AssetName, AssetName);
	}

	FString ObjectPathOf(const FString& PackageName, const FString& AssetName)
	{
		return FString::Printf(TEXT("%s.%s"), *PackageName, *AssetName);
	}

	FString ImportNameFor(const TCHAR* Prefix, const FString& SourceFile)
	{
		const FString Base = FPaths::GetBaseFilename(SourceFile);
		// Idempotent: a source file that is already named to the standard must not become T_T_Drape.
		return Base.StartsWith(Prefix) ? Base : FString(Prefix) + Base;
	}

	FString TextureName(const FString& SourceFile)
	{
		return ImportNameFor(TEXT("T_"), SourceFile);
	}

	FString StaticMeshName(const FString& SourceFile)
	{
		return ImportNameFor(TEXT("SM_"), SourceFile);
	}

	FString DrapeMaterialName(const FString& Identity)
	{
		return FString::Printf(TEXT("MI_Drape_%s"), *ShortIdentity(Identity));
	}

	FString LayerInfoName(const FString& Material)
	{
		return FString::Printf(TEXT("LI_%s"), *Material);
	}

	FString TreePointsTableName(const FString& Identity)
	{
		return FString::Printf(TEXT("DT_TreePoints_%s"), *ShortIdentity(Identity));
	}

	FString TreePointsRowName(int32 RowIndex)
	{
		return FString::Printf(TEXT("Tree_%d"), RowIndex);
	}

	FString ActorLabel(EActorKind Kind, const FString& Identity)
	{
		return FString::Printf(
			TEXT("%s%s_%s"), ActorLabelPrefix, ActorKindName(Kind), *ShortIdentity(Identity));
	}

	FString RoadSplineLabelPrefix(const FString& Identity)
	{
		return FString::Printf(TEXT("%sRoadSpline_%s_"), ActorLabelPrefix, *ShortIdentity(Identity));
	}

	FString RoadSplineLabel(const FString& Identity, int32 Index)
	{
		return FString::Printf(TEXT("%s%03d"), *RoadSplineLabelPrefix(Identity), Index);
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
