// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceLandcoverTypes.h"
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceTreePointsLogic.h"

#include "MantlePlaceEditorSettings.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/DataTable.h"
#include "Misc/FileHelper.h"
#include "UObject/Package.h"

namespace
{
FMantlePlaceTreeLevelEntry ReadLevelEntry(const TSharedPtr<FJsonObject>& Levels, const TCHAR* Name)
{
	using namespace MantlePlaceManifestJson;
	FMantlePlaceTreeLevelEntry Entry;
	const TSharedPtr<FJsonObject>* Member = GetObject(Levels, Name);
	if (Member == nullptr)
	{
		return Entry;
	}
	const FString SameAs = GetString(*Member, TEXT("same_as"));
	if (!SameAs.IsEmpty())
	{
		Entry.Form = EMantlePlaceTreeLevelForm::SameAs;
		Entry.SameAs = SameAs;
		return Entry;
	}
	if (const TSharedPtr<FJsonObject>* Cut = GetObject(*Member, TEXT("cut")))
	{
		const int32 Rows = GetInt(*Cut, TEXT("rows"));
		if (Rows > 0)
		{
			Entry.Form = EMantlePlaceTreeLevelForm::RowCut;
			Entry.Rows = Rows;
		}
		return Entry;
	}
	const FString Path = GetString(*Member, TEXT("path"));
	if (!Path.IsEmpty())
	{
		Entry.Form = EMantlePlaceTreeLevelForm::Pointer;
		Entry.Path = Path;
		Entry.Sha256 = GetString(*Member, TEXT("sha256"));
		Entry.Count = GetInt(*Member, TEXT("point_count"));
		Entry.Crs = GetString(*Member, TEXT("crs"));
		Entry.Units = GetString(*Member, TEXT("units"));
		Entry.HorizontalUnits = GetString(*Member, TEXT("horizontal_units"));
	}
	return Entry;
}

EMantlePlaceTreeLevel RequestedLevel()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return Settings != nullptr ? Settings->FoliageTreeLevel : EMantlePlaceTreeLevel::Max;
}

class FFoliagePointsHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("foliage_points")); }
	EMantlePlaceLayerFamily LayerFamily() const override { return EMantlePlaceLayerFamily::Foliage; }
	FText DisplayName() const override { return NSLOCTEXT("MantlePlaceImporter", "ArtifactFoliagePoints", "Tree points"); }

	bool ParseFields(const FMantlePlaceParseInput& In, FMantlePlaceVaultManifest& M, FString& OutError) const override
	{
		using namespace MantlePlaceManifestJson;
		if (const TSharedPtr<FJsonObject>* FoliagePointsPtr = GetObject(In.Unreal, TEXT("foliage_points")))
		{
			M.Foliage.Path = GetString(*FoliagePointsPtr, TEXT("path"));
			M.Foliage.Sha256 = GetString(*FoliagePointsPtr, TEXT("sha256"));
			M.Foliage.Count = GetInt(*FoliagePointsPtr, TEXT("point_count"));
			M.Foliage.Crs = GetString(*FoliagePointsPtr, TEXT("crs"));
			M.Foliage.Units = GetString(*FoliagePointsPtr, TEXT("units"));
			M.Foliage.HorizontalUnits = GetString(*FoliagePointsPtr, TEXT("horizontal_units"));
			if (const TSharedPtr<FJsonObject>* LevelsPtr = GetObject(*FoliagePointsPtr, TEXT("levels")))
			{
				M.Foliage.LevelMax = ReadLevelEntry(*LevelsPtr, TEXT("MAX"));
				M.Foliage.LevelMed = ReadLevelEntry(*LevelsPtr, TEXT("MED"));
				M.Foliage.LevelMin = ReadLevelEntry(*LevelsPtr, TEXT("MIN"));
			}
		}
		if (const TSharedPtr<FJsonObject>* LandcoverPtr = GetObject(In.Root, TEXT("landcover")))
		{
			if (const TSharedPtr<FJsonObject>* TreePointsPtr = GetObject(*LandcoverPtr, TEXT("tree_points")))
			{
				M.Foliage.FoliageTypeVocabulary = GetString(*TreePointsPtr, TEXT("foliage_type_vocabulary"));
			}
		}
		M.Foliage.bPresent = !M.Foliage.Path.IsEmpty();
		return true;
	}

	bool IsPresent(const FMantlePlaceVaultManifest& M) const override { return M.Foliage.bPresent; }

	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Foliage.bPresent && !M.Foliage.Path.IsEmpty())
		{
			const FMantlePlaceResolvedTreeLevel Level = FMantlePlaceTreePointsLogic::ResolveLevel(M.Foliage, RequestedLevel());
			Out.Add(FMantlePlaceDeclaredArtifact{ Level.Path, Level.Sha256 });
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.Foliage.bPresent)
		{
			return;
		}
		const FMantlePlaceResolvedTreeLevel Level =
			FMantlePlaceTreePointsLogic::ResolveLevel(Manifest.Foliage, RequestedLevel());
		if (!Level.FallbackReason.IsEmpty())
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Tree points: the requested level is not available (%s); importing MAX."), *Level.FallbackReason));
		}
		TArray<uint8> CsvBytes;
		if (!Ctx.ReadBytes(Level.Path, CsvBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Tree points skipped: bundle is missing %s."), *Level.Path));
			return;
		}
		FString CsvText, TreesError;
		FFileHelper::BufferToString(CsvText, CsvBytes.GetData(), CsvBytes.Num());
		CsvText = FMantlePlaceTreePointsLogic::CutRows(CsvText, Level.RowLimit);
		FMantlePlaceTreePointsFrame Frame = Manifest.GetFoliagePointsFrame();
		if (Level.bOwnFrame)
		{
			Frame.Crs = Level.Crs;
			Frame.Units = Level.Units;
			Frame.HorizontalUnits = Level.HorizontalUnits;
		}
		TArray<FMantlePlaceTreePointRow> Rows;
		FString OutsideNote;
		const bool bSiteFrame = Manifest.Foliage.bSiteFrame;
		const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
			CsvText, bSiteFrame ? 0.0 : Manifest.Georef.OriginEastingM, bSiteFrame ? 0.0 : Manifest.Georef.OriginNorthingM, Frame,
			Manifest.GetAoiSizeUeCm(),
			Level.DeclaredCount, Rows, TreesError, Manifest.Foliage.FoliageTypeVocabulary, &OutsideNote);
		if (Outcome == EMantlePlaceTreePointsOutcome::CountMismatch)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Tree points FAILED (%s): %s"), *Level.Path, *TreesError));
			Ctx.bAllRequestedSucceeded = false;
			return;
		}
		if (Outcome == EMantlePlaceTreePointsOutcome::Parsed)
		{
			if (!OutsideNote.IsEmpty())
			{
				Ctx.Log.Add(FString::Printf(TEXT("Tree points (%s): %s"), *Level.Path, *OutsideNote));
			}
			for (FMantlePlaceTreePointRow& Row : Rows)
			{
				if (bSiteFrame && Row.bGroundKnown)
				{
					Row.GroundZM += static_cast<float>(Manifest.Foliage.SiteHeightM);
					Row.Position.Z += Manifest.Foliage.SiteHeightM * 100.0;
				}
				Row.Position = Manifest.ToLevelFrame(Row.Position);
			}
		}
		if (Outcome != EMantlePlaceTreePointsOutcome::Parsed)
		{
			Ctx.Log.Add(FString::Printf(
				TEXT("Tree points skipped (%s): %s"), *Level.Path, *TreesError));
			return;
		}

		const FString AssetName = MantlePlaceImportNaming::TreePointsTableName();
		const FString PackageName =
			MantlePlaceImportNaming::SubfolderPath(
				Ctx.DestPackagePath, MantlePlaceImportNaming::ESubfolder::Landcover)
			/ AssetName;
		UPackage* Package = CreatePackage(*PackageName);
		UDataTable* Table = NewObject<UDataTable>(Package, FName(*AssetName), RF_Public | RF_Standalone);
		Table->RowStruct = FMantlePlaceTreePointRow::StaticStruct();
		Table->SetFlags(RF_Transactional);
		for (int32 RowIndex = 0; RowIndex < Rows.Num(); ++RowIndex)
		{
			Table->AddRow(
				FName(*MantlePlaceImportNaming::TreePointsRowName(RowIndex)), Rows[RowIndex]);
		}
		FAssetRegistryModule::AssetCreated(Table);
		Table->MarkPackageDirty();
		Ctx.TreePointsTable = Table;
		int32 ShrubCount = 0, GroundUnknownCount = 0;
		for (const FMantlePlaceTreePointRow& Row : Rows)
		{
			ShrubCount += Row.FoliageType == EMantlePlaceFoliageType::Shrub ? 1 : 0;
			GroundUnknownCount += Row.bGroundKnown ? 0 : 1;
		}
		Ctx.Log.Add(FString::Printf(
			TEXT("Tree points imported (%s level, %d rows -> %s: %d shrub, %d without a measured ground height, %s)."),
			Level.Applied == EMantlePlaceTreeLevel::Max ? TEXT("MAX") : (Level.Applied == EMantlePlaceTreeLevel::Med ? TEXT("MED") : TEXT("MIN")),
			Rows.Num(), *AssetName, ShrubCount, GroundUnknownCount,
			Level.Sha256.IsEmpty()
				? TEXT("valid but unverified: the manifest publishes no digest for it")
				: TEXT("verified against the manifest's sha256")));
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliagePoints() { return MakeUnique<FFoliagePointsHandler>(); }
}
