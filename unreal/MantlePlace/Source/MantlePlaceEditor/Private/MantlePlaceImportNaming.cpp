// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportNaming.h"

#include "AssetToolsModule.h"
#include "Engine/DataTable.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "GameFramework/Actor.h"
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
			case EActorKind::Foliage:   return TEXT("Foliage");
			}
			checkNoEntry();
			return TEXT("Actor");
		}

		const TCHAR* const ActorLabelPrefix = TEXT("MP_");

		const TCHAR* const OutlinerRoot = TEXT("MantlePlace");

		const TCHAR* const ImportTagKey = TEXT("mantleplace_import");

		bool IsUsableIdentityChar(const TCHAR Char)
		{
			return FChar::IsAlnum(Char) || Char == TEXT('-') || Char == TEXT('_') || Char == TEXT('+');
		}

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

	FString FrameTagPrefix()
	{
		return TEXT("mantleplace_frame=");
	}

	FString FrameTag(const FString& Value)
	{
		return FrameTagPrefix() + Value;
	}

	FString OdblFolder(const FString& Identity)
	{
		return OutlinerFolder(Identity) / TEXT("ODbL");
	}

	FString OdblLayerName()
	{
		return TEXT("ODbL");
	}

	FName OdblTag()
	{
		return FName(TEXT("mantleplace_odbl"));
	}

	FName CollisionOffTag()
	{
		return FName(TEXT("mantleplace_collision_off"));
	}

	FString EquivalenceTagPrefix()
	{
		return TEXT("mantleplace_equivalent=");
	}

	FString EquivalenceTag(const FString& Group)
	{
		return EquivalenceTagPrefix() + Group;
	}

	bool TagValue(const AActor& Actor, const FString& Prefix, FString& OutValue)
	{
		for (const FName& Tag : Actor.Tags)
		{
			const FString Text = Tag.ToString();
			if (Text.StartsWith(Prefix))
			{
				OutValue = Text.Mid(Prefix.Len());
				return true;
			}
		}
		return false;
	}

	FString CreditLineTagPrefix()
	{
		return TEXT("mantleplace_credit=");
	}

	FString CreditLineTag(const FString& Line)
	{
		return CreditLineTagPrefix() + Line;
	}

	FString CreditPartTagPrefix()
	{
		return TEXT("mantleplace_credit_part=");
	}

	FString CreditActorTag(const FString& Identity)
	{
		return TEXT("mantleplace_credit_actor=") + Identity;
	}

	FString CreditActorLabel(const FString& Identity)
	{
		return FString::Printf(TEXT("%sCredit_%s"), ActorLabelPrefix, *ShortIdentity(Identity));
	}

	FString ExtentBoxLabel()
	{
		return FString(ActorLabelPrefix) + TEXT("ExtentBox");
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
		ContentRoot.ParseIntoArray(Segments, TEXT("/"), false);
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

	FString SharedLayersPath(const FString& ContentRoot)
	{
		return ContentRoot / TEXT("Layers");
	}

	FString DataLayerPackageName(const FString& ContentRoot, const FString& FamilyName)
	{
		return SharedLayersPath(ContentRoot) / DataLayerAssetName(FamilyName);
	}

	FString HLODLayerPackageName(const FString& ContentRoot, const FString& FamilyName)
	{
		return SharedLayersPath(ContentRoot) / HLODLayerAssetName(FamilyName);
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

	FString DataLayerAssetName(const FString& FamilyName)
	{
		return FString::Printf(TEXT("DL_%s"), *FamilyName);
	}

	FString HLODLayerAssetName(const FString& FamilyName)
	{
		return FString::Printf(TEXT("HLOD_%s"), *FamilyName);
	}

	FString TreePointsTableName()
	{
		return TEXT("DT_TreePoints");
	}

	FString FoliageGraphName()
	{
		return TEXT("PCG_TreeScatter");
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

	FString PublicRecordLabel(const FString& Identity, const FString& Family)
	{
		FString Words;
		bool bWordStart = true;
		for (const TCHAR Ch : Family)
		{
			if (!FChar::IsAlnum(Ch))
			{
				bWordStart = true;
				continue;
			}
			Words.AppendChar(bWordStart ? FChar::ToUpper(Ch) : Ch);
			bWordStart = false;
		}
		if (Words.IsEmpty())
		{
			Words = TEXT("Records");
		}
		return FString::Printf(TEXT("%sRecord_%s_%s"), ActorLabelPrefix, *Words, *ShortIdentity(Identity));
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
