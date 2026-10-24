// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// `unreal.foliage_points` (HPS-32/HPS-33): tree points. Resolved from the bundle's own pointer —
// never layout.tree_points or landcover.tree_points, which are other blocks' pointers to the same
// layer. Absence means the bundle simply doesn't ship the layer (base tier / treeless AOI), not an
// error. Rows land in a UDataTable under the bundle's content folder: PCG-ready scatter input, no
// actors spawned. The table's columns are a documented contract (unreal/docs/foliage-table-contract.md):
// a row carries the platform's foliage type, its sizes in the engine's unit as well as the published
// metres, and whether the ground height was measured -- decoded where the CSV is read, not left raw.
//
// The bytes reaching Import have already been through the integrity pre-check. What is still checked
// HERE is the row count — the digest proves the bytes, the count proves the rows this reader made of
// them — and the frame (HPS-53): the CRS and units the pointer states must be this host's, and the
// rows are held against the landscape extent the manifest publishes as the backstop — the whole of
// the check on a bundle older than MPB 1.3.0, whose pointer states no frame. Either way a file not in
// this frame is refused by name.

#include "MantlePlaceArtifactHandler.h"

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceLandcoverTypes.h" // runtime: FMantlePlaceTreePointRow
#include "MantlePlaceManifestJson.h"
#include "MantlePlaceTreePointsLogic.h"

#include "MantlePlaceEditorSettings.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/DataTable.h"
#include "Misc/FileHelper.h"
#include "UObject/Package.h"

namespace
{
// One `levels.<LEVEL>` member, verbatim. A same_as wins over a cut over a pointer; a member that
// states none of them is Absent, which for MAX is the entry itself and for MED and MIN is a level
// the bundle does not publish.
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
		// A cut by anything but `rows` (or by rows that are not positive) is a form this build does
		// not know: the level stays Absent and fails closed to MAX.
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

// The level the project asked for. A handler reading the default settings object is how the
// foliage scatter beside this one reads its meshes.
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
			// Both OPTIONAL in the schema, on purpose: the facts come from a best-effort sidecar, so
			// requiring them would refuse bundles that are perfectly importable. Absent therefore means
			// "unknown" and never "corrupt" (spec/format.md section 5) — the digest is skipped and the
			// row count is not cross-checked, and the importer says which of the two it did.
			M.Foliage.Sha256 = GetString(*FoliagePointsPtr, TEXT("sha256"));
			M.Foliage.Count = GetInt(*FoliagePointsPtr, TEXT("point_count"));
			// The file's frame, as stated (MPB 1.3.0). Read verbatim and judged nowhere here: whether
			// it is this host's frame is FMantlePlaceTreePointsLogic's call, and a frame that is not
			// refuses the layer by name rather than the manifest (HPS-53).
			M.Foliage.Crs = GetString(*FoliagePointsPtr, TEXT("crs"));
			M.Foliage.Units = GetString(*FoliagePointsPtr, TEXT("units"));
			M.Foliage.HorizontalUnits = GetString(*FoliagePointsPtr, TEXT("horizontal_units"));
			// The published levels (MPB 1.9.0). Read as stated; which one is imported is the
			// project's choice, and FMantlePlaceTreePointsLogic::ResolveLevel decides what it means.
			if (const TSharedPtr<FJsonObject>* LevelsPtr = GetObject(*FoliagePointsPtr, TEXT("levels")))
			{
				M.Foliage.LevelMax = ReadLevelEntry(*LevelsPtr, TEXT("MAX"));
				M.Foliage.LevelMed = ReadLevelEntry(*LevelsPtr, TEXT("MED"));
				M.Foliage.LevelMin = ReadLevelEntry(*LevelsPtr, TEXT("MIN"));
			}
		}
		// The vocabulary the CSV's `foliage_type` values come from (MPB 1.2.0). Published once, on the
		// shared tree-points block, so this is the one read from outside `unreal`; it is the meaning of
		// a column in this block's own file, not a second pointer to it. Verbatim, and judged nowhere:
		// a vocabulary this build was not taught still has its known values mapped.
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

	// The tree points, no longer the exception: the digest comes off `unreal.foliage_points`, this
	// host's own block -- never `landcover.tree_points`, which carries the same value addressed to
	// somebody else (HPS-33). It stays OPTIONAL in the schema, so a bundle without one is imported
	// and reported unverified rather than refused.
	void CollectDeclared(const FMantlePlaceVaultManifest& M, TArray<FMantlePlaceDeclaredArtifact>& Out) const override
	{
		if (M.Foliage.bPresent && !M.Foliage.Path.IsEmpty())
		{
			// The file the chosen level reads: the MAX file for MAX and for a row cut of it, a
			// level's own file for an older bundle's per-level pointer.
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
		// A row cut keeps the first rows of the one ranked file: a row's number is its rank.
		CsvText = FMantlePlaceTreePointsLogic::CutRows(CsvText, Level.RowLimit);
		// A per-level file states its own frame; a cut and MAX read the block's.
		FMantlePlaceTreePointsFrame Frame = Manifest.GetFoliagePointsFrame();
		if (Level.bOwnFrame)
		{
			Frame.Crs = Level.Crs;
			Frame.Units = Level.Units;
			Frame.HorizontalUnits = Level.HorizontalUnits;
		}
		TArray<FMantlePlaceTreePointRow> Rows;
		const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
			CsvText, Manifest.Georef.OriginEastingM, Manifest.Georef.OriginNorthingM, Frame,
			Manifest.GetAoiSizeUeCm(), // zero when the block publishes no landscape transform
			Level.DeclaredCount, Rows, TreesError, Manifest.Foliage.FoliageTypeVocabulary);
		if (Outcome == EMantlePlaceTreePointsOutcome::CountMismatch)
		{
			// A count that disagrees with the payload FAILS the import rather than skipping the
			// layer: the rows that did parse are a silent subset, and a scatter built from a
			// subset reads as a sparse forest rather than as an error.
			Ctx.Log.Add(FString::Printf(
				TEXT("Tree points FAILED (%s): %s"), *Level.Path, *TreesError));
			Ctx.bAllRequestedSucceeded = false;
			return;
		}
		if (Outcome != EMantlePlaceTreePointsOutcome::Parsed)
		{
			// A drifted column contract stays a SKIP, as it always has been. It says the ETL
			// changed this payload's shape, which is not a reason to report the terrain, the
			// imagery and the buildings beside it as failed. A file that is not in this host's
			// frame is the same news (HPS-53): a NAMED skip, no table made, so nothing downstream
			// scatters a forest a thousand kilometres off site.
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
		// RF_Transactional is set AFTER RowStruct, not passed to NewObject. A transactional
		// object constructed while a transaction is open is serialized into the undo buffer
		// there and then, and UDataTable::Serialize logs an Error when it is written with no
		// RowStruct -- which is precisely its state on the line between these two. Latent
		// until the import's transaction started surviving to the end (see
		// MantlePlaceImportNaming::ImportNameFor); before that the buffer was purged out
		// from under it and the snapshot never happened.
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
		// Three states, said out loud. A payload the platform published no digest for is
		// VALID BUT UNVERIFIED — reporting it as verified is a lie and reporting it as
		// corrupt makes every sidecar-less bundle un-importable (spec/format.md section 5).
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
