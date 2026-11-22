// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class AActor;
class UObject;

namespace MantlePlaceImportNaming
{
	enum class ESubfolder : uint8
	{
		Imagery,
		Mesh,
		Buildings,
		CoverageRasters,
		Landcover,
	};

	enum class EActorKind : uint8
	{
		Landscape,
		Mesh,
		Buildings,
		Foliage,
	};

	FString ResolveIdentity(const FString& OrderId, const FString& ManifestSha256);

	bool IsUsableIdentity(const FString& Identity);

	FString ShortIdentity(const FString& Identity);

	FString OutlinerFolder(const FString& Identity);

	FString ImportTag(const FString& Identity);

	FString ImportTagPrefix();

	FString FrameTag(const FString& Value);
	FString FrameTagPrefix();

	FString OdblFolder(const FString& Identity);

	FString OdblLayerName();

	FName OdblTag();

	FName CollisionOffTag();

	FString EquivalenceTag(const FString& Group);
	FString EquivalenceTagPrefix();

	bool TagValue(const AActor& Actor, const FString& Prefix, FString& OutValue);

	FString CreditLineTag(const FString& Line);
	FString CreditLineTagPrefix();

	FString CreditPartTagPrefix();

	FString CreditActorTag(const FString& Identity);

	FString CreditActorLabel(const FString& Identity);

	const TCHAR* DefaultContentRoot();

	bool IsUsableContentRoot(const FString& ContentRoot);

	FString ResolveContentRoot(const FString& ConfiguredRoot);

	FString ImportRoot(const FString& ContentRoot, const FString& Identity);

	FString SubfolderPath(const FString& ImportRootPath, ESubfolder Subfolder);

	FString SharedLayersPath(const FString& ContentRoot);

	FString DataLayerPackageName(const FString& ContentRoot, const FString& FamilyName);

	FString HLODLayerPackageName(const FString& ContentRoot, const FString& FamilyName);

	FString ObjectPathIn(const FString& PackagePath, const FString& AssetName);

	FString ObjectPathOf(const FString& PackageName, const FString& AssetName);

	FString ImportNameFor(const TCHAR* Prefix, const FString& SourceFile);

	FString TextureName(const FString& SourceFile);

	FString StaticMeshName(const FString& SourceFile);

	FString DrapeMaterialName();

	FString LayerInfoName(const FString& Material);

	FString DataLayerAssetName(const FString& FamilyName);

	FString HLODLayerAssetName(const FString& FamilyName);

	FString TreePointsTableName();

	FString FoliageGraphName();

	FString TreePointsRowName(int32 RowIndex);

	FString ActorLabel(EActorKind Kind, const FString& Identity);

	FString ExtentBoxLabel();

	FString RoadSplineLabelPrefix(const FString& Identity);

	FString RoadSplineLabel(const FString& Identity, int32 Index);

	FString PublicRecordLabel(const FString& Identity, const FString& Family);

	void RenameToConvention(UObject* Asset);
}
