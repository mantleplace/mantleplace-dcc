// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceTreePointsLogic.h"

#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest::ProjectedToUeCm

EMantlePlaceTreePointsOutcome FMantlePlaceTreePointsLogic::ParseCsv(
    const FString& CsvText,
    double OriginEastingM,
    double OriginNorthingM,
    const FVector2D& LandscapeSpanUeCm,
    int32 DeclaredPointCount,
    TArray<FMantlePlaceTreePointRow>& OutRows,
    FString& OutError)
{
	OutRows.Reset();

	TArray<FString> Lines;
	CsvText.ParseIntoArrayLines(Lines, /*bCullEmpty*/ true);
	if (Lines.Num() == 0)
	{
		OutError = TEXT("TreePoints.csv is empty: it has no header row naming the columns x, y, ground_z, "
		                "height_m and crown_radius_m.");
		return EMantlePlaceTreePointsOutcome::HeaderUnrecognised;
	}

	// Columns are found by header NAME, never by position. The manifest publishes the column names
	// (`landcover.tree_points.columns`), so the names are the contract and the order is not: a
	// column this reader does not know is ignored, and only a missing required one is a drift.
	static const TCHAR* const RequiredColumns[] = {
	    TEXT("x"), TEXT("y"), TEXT("ground_z"), TEXT("height_m"), TEXT("crown_radius_m")};
	enum : int32 { ColumnX, ColumnY, ColumnGroundZ, ColumnHeight, ColumnCrown, RequiredColumnCount };

	TArray<FString> HeaderFields;
	Lines[0].ParseIntoArray(HeaderFields, TEXT(","), /*bCullEmpty*/ false);
	int32 ColumnIndex[RequiredColumnCount];
	TArray<FString> Missing;
	int32 WidestIndex = 0;
	for (int32 Required = 0; Required < RequiredColumnCount; ++Required)
	{
		ColumnIndex[Required] = HeaderFields.IndexOfByPredicate([&](const FString& Field) {
			return Field.TrimStartAndEnd().Equals(RequiredColumns[Required], ESearchCase::IgnoreCase);
		});
		if (ColumnIndex[Required] == INDEX_NONE)
		{
			Missing.Add(FString::Printf(TEXT("\"%s\""), RequiredColumns[Required]));
		}
		WidestIndex = FMath::Max(WidestIndex, ColumnIndex[Required]);
	}
	if (Missing.Num() > 0)
	{
		OutError = FString::Printf(
		    TEXT("TreePoints.csv has no %s column (it needs x, y, ground_z, height_m and crown_radius_m, "
		         "in any order). The ETL column contract changed; the tree-points layer is skipped."),
		    *FString::Join(Missing, TEXT(", ")));
		return EMantlePlaceTreePointsOutcome::HeaderUnrecognised;
	}

	// HPS-53: with no published extent there is nothing to hold the file against, and an unstated
	// frame is never assumed to match. Refused here rather than after the rows, because no row
	// could change the answer.
	if (LandscapeSpanUeCm.X <= 0.0 || LandscapeSpanUeCm.Y <= 0.0)
	{
		OutError = TEXT("TreePoints.csv states no CRS and no unit, and the manifest's Unreal block publishes "
		                "no landscape extent to check its coordinates against, so the file cannot be shown "
		                "to be in this host's metric UTM frame. Nothing is converted or assumed.");
		return EMantlePlaceTreePointsOutcome::Unplaceable;
	}
	const double HalfSpanNorthCm = LandscapeSpanUeCm.X / 2.0;
	const double HalfSpanEastCm = LandscapeSpanUeCm.Y / 2.0;
	int32 OutsideCount = 0;
	FString FirstOutsideX, FirstOutsideY;

	OutRows.Reserve(Lines.Num() - 1);
	for (int32 LineIndex = 1; LineIndex < Lines.Num(); ++LineIndex)
	{
		TArray<FString> Fields;
		Lines[LineIndex].ParseIntoArray(Fields, TEXT(","), /*bCullEmpty*/ false);
		if (Fields.Num() <= WidestIndex || !Fields[ColumnIndex[ColumnX]].IsNumeric()
		    || !Fields[ColumnIndex[ColumnY]].IsNumeric())
		{
			continue; // one malformed row must not drop the whole layer
		}

		const double UtmX = FCString::Atod(*Fields[ColumnIndex[ColumnX]]);
		const double UtmY = FCString::Atod(*Fields[ColumnIndex[ColumnY]]);
		// ground_z is deliberately empty when the DEM had no data under the point.
		const FString& GroundField = Fields[ColumnIndex[ColumnGroundZ]];
		const double GroundZM = GroundField.IsEmpty() ? 0.0 : FCString::Atod(*GroundField);

		FMantlePlaceTreePointRow Row;
		// Same frame math as the drape/mesh placement, through the one helper that owns it.
		Row.Position = FMantlePlaceVaultManifest::ProjectedToUeCm(
		    UtmX - OriginEastingM, UtmY - OriginNorthingM, GroundZM);
		Row.HeightM = FCString::Atof(*Fields[ColumnIndex[ColumnHeight]]);
		Row.CrownRadiusM = FCString::Atof(*Fields[ColumnIndex[ColumnCrown]]);
		Row.GroundZM = static_cast<float>(GroundZM);
		OutRows.Add(Row);

		// The landscape is centred on the origin, so its extent in this frame is +/- half its span.
		// A comparison of two published values, in the units they were published in.
		if (FMath::Abs(Row.Position.X) > HalfSpanNorthCm || FMath::Abs(Row.Position.Y) > HalfSpanEastCm)
		{
			if (OutsideCount++ == 0)
			{
				FirstOutsideX = Fields[ColumnIndex[ColumnX]];
				FirstOutsideY = Fields[ColumnIndex[ColumnY]];
			}
		}
	}

	// HPS-53: a point outside the extent this host's own block publishes is not in this frame, and
	// the frame belongs to the FILE — so the rows that happened to land inside go too. The failure
	// this guards is arithmetic that succeeds: foot State Plane coordinates minus a metre UTM origin
	// is a finite number a thousand kilometres away that looks exactly like a position. Never a
	// conversion (HPS-33): nothing here scales a unit or reprojects to reconcile the mismatch.
	if (OutsideCount > 0)
	{
		OutError = FString::Printf(
		    TEXT("TreePoints.csv is not in this host's frame: %d of %d point(s) fall outside the landscape "
		         "extent the manifest publishes (%.1f m east-west by %.1f m north-south, centred on the "
		         "origin); the first is x=%s, y=%s. Unreal places metric UTM coordinates only, and a file "
		         "stated on another grid or in another unit is refused rather than converted."),
		    OutsideCount, OutRows.Num(), LandscapeSpanUeCm.Y / 100.0, LandscapeSpanUeCm.X / 100.0,
		    *FirstOutsideX, *FirstOutsideY);
		OutRows.Reset();
		return EMantlePlaceTreePointsOutcome::Unplaceable;
	}

	// The manifest's own row count, checked against what this reader produced. A mismatch is a
	// failure rather than a warning: the rows it did produce are a silent subset of the layer, and
	// a foliage scatter built from a subset looks like a sparse forest, not like an error.
	if (DeclaredPointCount > 0 && OutRows.Num() != DeclaredPointCount)
	{
		OutError = FString::Printf(
		    TEXT("TreePoints.csv parsed %d row(s) but the manifest declares point_count %d. The "
		         "payload and the manifest disagree; refusing to import a subset of the layer."),
		    OutRows.Num(), DeclaredPointCount);
		OutRows.Reset();
		return EMantlePlaceTreePointsOutcome::CountMismatch;
	}
	return EMantlePlaceTreePointsOutcome::Parsed;
}
