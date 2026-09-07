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

		// The fixed top segment of the outliner folder. Spelled in full: this is a folder name a
		// user reads with room to read it, not a label in a narrow column.
		const TCHAR* const OutlinerRoot = TEXT("MantlePlace");

		// Tags follow the idiom the road splines already use: lower_snake key, `=`, value.
		const TCHAR* const ImportTagKey = TEXT("mantleplace_import");

		// An identity has to survive being a single package-path segment of a directory that gets
		// force-deleted. Letters, digits, and the three separators an order id or a hash can
		// contain. Everything else — a slash, a colon, a dot, whitespace — is refused rather than
		// stripped, because stripping invents an identity nobody chose.
		bool IsUsableIdentityChar(const TCHAR Char)
		{
			return FChar::IsAlnum(Char) || Char == TEXT('-') || Char == TEXT('_') || Char == TEXT('+');
		}

		// Long enough that the truncation to eight is meaningful, and long enough that a stray
		// fragment of a field cannot pass for one.
		constexpr int32 MinIdentityLength = 8;
	}

	bool IsUsableIdentity(const FString& Identity)
	{
		if (Identity.Len() < MinIdentityLength)
		{
			return false;
		}
		for (const TCHAR Char : Identity)
		{
			if (!IsUsableIdentityChar(Char))
			{
				return false;
			}
		}
		return true;
	}

	FString ResolveIdentity(const FString& OrderId, const FString& ManifestSha256)
	{
		// The order first, always: it is the only one of the two that is stable across a rebuild,
		// which is the whole point of ADR 0002.
		if (IsUsableIdentity(OrderId))
		{
			return OrderId;
		}
		if (IsUsableIdentity(ManifestSha256))
		{
			return ManifestSha256;
		}
		return FString();
	}

	FString ShortIdentity(const FString& Identity)
	{
		return Identity.Left(8);
	}

	FString OutlinerFolder(const FString& Identity)
	{
		return FString(OutlinerRoot) / ShortIdentity(Identity);
	}

	FString ImportTag(const FString& Identity)
	{
		return ImportTagPrefix() + Identity;
	}

	FString ImportTagPrefix()
	{
		return FString::Printf(TEXT("%s="), ImportTagKey);
	}

	const TCHAR* DefaultContentRoot()
	{
		return TEXT("/Game/MantlePlace");
	}

	bool IsUsableContentRoot(const FString& ContentRoot)
	{
		if (ContentRoot.Len() < 2 || !ContentRoot.StartsWith(TEXT("/")))
		{
			return false;
		}
		if (ContentRoot.EndsWith(TEXT("/")))
		{
			return false;
		}
		TArray<FString> Segments;
		ContentRoot.ParseIntoArray(Segments, TEXT("/"), /*InCullEmpty*/ false);
		// ParseIntoArray on a leading-slash path yields an empty first element; every element after
		// it must be a real segment. An empty one means a doubled slash, which would collapse.
		for (int32 Index = 1; Index < Segments.Num(); ++Index)
		{
			const FString& Segment = Segments[Index];
			if (Segment.IsEmpty() || Segment == TEXT(".") || Segment == TEXT(".."))
			{
				return false;
			}
			for (const TCHAR Char : Segment)
			{
				if (!IsUsableIdentityChar(Char) && Char != TEXT('.'))
				{
					return false;
				}
			}
		}
		return Segments.Num() >= 2;
	}

	FString ResolveContentRoot(const FString& ConfiguredRoot)
	{
		return IsUsableContentRoot(ConfiguredRoot) ? ConfiguredRoot : FString(DefaultContentRoot());
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

	FString DrapeMaterialName()
	{
		return TEXT("MI_Drape");
	}

	FString LayerInfoName(const FString& Material)
	{
		return FString::Printf(TEXT("LI_%s"), *Material);
	}

	FString TreePointsTableName()
	{
		return TEXT("DT_TreePoints");
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
