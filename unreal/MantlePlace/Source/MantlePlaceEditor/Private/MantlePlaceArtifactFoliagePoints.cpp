// Copyright Mantle Place. All Rights Reserved.

// `unreal.foliage_points` (HPS-32/HPS-33): tree points. Resolved from the bundle's own pointer —
// never layout.tree_points or landcover.tree_points, which are other blocks' pointers to the same
// layer. Absence means the bundle simply doesn't ship the layer (base tier / treeless AOI), not an
// error. Rows land in a UDataTable under the bundle's content folder: PCG-ready scatter input, no
// actors spawned.
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

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/DataTable.h"
#include "Misc/FileHelper.h"
#include "UObject/Package.h"

namespace
{
class FFoliagePointsHandler final : public IMantlePlaceArtifactHandler
{
public:
	FName Id() const override { return FName(TEXT("foliage_points")); }
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
			Out.Add(FMantlePlaceDeclaredArtifact{ M.Foliage.Path, M.Foliage.Sha256 });
		}
	}

	void Import(FMantlePlaceImportContext& Ctx) override
	{
		const FMantlePlaceVaultManifest& Manifest = Ctx.Manifest;
		if (!Manifest.Foliage.bPresent)
		{
			return;
		}
		TArray<uint8> CsvBytes;
		if (!Ctx.ReadBytes(Manifest.Foliage.Path, CsvBytes))
		{
			Ctx.Log.Add(FString::Printf(TEXT("Tree points skipped: bundle is missing %s."), *Manifest.Foliage.Path));
			return;
		}
		FString CsvText, TreesError;
		FFileHelper::BufferToString(CsvText, CsvBytes.GetData(), CsvBytes.Num());
		TArray<FMantlePlaceTreePointRow> Rows;
		const EMantlePlaceTreePointsOutcome Outcome = FMantlePlaceTreePointsLogic::ParseCsv(
			CsvText, Manifest.Georef.OriginEastingM, Manifest.Georef.OriginNorthingM, Manifest.GetFoliagePointsFrame(),
			Manifest.GetAoiSizeUeCm(), // zero when the block publishes no landscape transform
			Manifest.Foliage.Count, Rows, TreesError);
		if (Outcome == EMantlePlaceTreePointsOutcome::CountMismatch)
		{
			// A count that disagrees with the payload FAILS the import rather than skipping the
			// layer: the rows that did parse are a silent subset, and a scatter built from a
			// subset reads as a sparse forest rather than as an error.
			Ctx.Log.Add(FString::Printf(
				TEXT("Tree points FAILED (%s): %s"), *Manifest.Foliage.Path, *TreesError));
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
				TEXT("Tree points skipped (%s): %s"), *Manifest.Foliage.Path, *TreesError));
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
		// Three states, said out loud. A payload the platform published no digest for is
		// VALID BUT UNVERIFIED — reporting it as verified is a lie and reporting it as
		// corrupt makes every sidecar-less bundle un-importable (spec/format.md section 5).
		Ctx.Log.Add(FString::Printf(
			TEXT("Tree points imported (%d rows -> %s, %s)."),
			Rows.Num(), *AssetName,
			Manifest.Foliage.Sha256.IsEmpty()
				? TEXT("valid but unverified: the manifest publishes no digest for it")
				: TEXT("verified against the manifest's sha256")));
	}
};
}

namespace MantlePlaceArtifacts
{
TUniquePtr<IMantlePlaceArtifactHandler> MakeFoliagePoints() { return MakeUnique<FFoliagePointsHandler>(); }
}
