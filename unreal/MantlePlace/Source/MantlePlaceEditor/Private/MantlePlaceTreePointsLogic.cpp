// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTreePointsLogic.h"

#include "MantlePlaceImportManifest.h" // FMantlePlaceVaultManifest::ProjectedToUeCm

EMantlePlaceTreePointsOutcome FMantlePlaceTreePointsLogic::ParseCsv(
    const FString& CsvText,
    double OriginEastingM,
    double OriginNorthingM,
    const FMantlePlaceTreePointsFrame& Frame,
    const FVector2D& LandscapeSpanUeCm,
    int32 DeclaredPointCount,
    TArray<FMantlePlaceTreePointRow>& OutRows,
    FString& OutError,
    const FString& FoliageTypeVocabulary)
{
	OutRows.Reset();

	TArray<FString> Lines;
	CsvText.ParseIntoArrayLines(Lines, /*bCullEmpty*/ true);
	if (Lines.Num() == 0)
	{
		OutError = TEXT("The tree-point file is empty: it has no header row naming the columns x, y, ground_z, "
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
	// The optional sixth column. Read only where the manifest names the vocabulary its values come
	// from: a manifest that names none has no such column, so a header that carries one anyway is
	// not trusted to mean it. The vocabulary id itself is not judged (see the header).
	const int32 FoliageColumn = HeaderFields.IndexOfByPredicate([](const FString& Field) {
		return Field.TrimStartAndEnd().Equals(TEXT("foliage_type"), ESearchCase::IgnoreCase);
	});
	const bool bReadFoliageType = FoliageColumn != INDEX_NONE && !FoliageTypeVocabulary.IsEmpty();

	if (Missing.Num() > 0)
	{
		OutError = FString::Printf(
		    TEXT("The tree-point file has no %s column (it needs x, y, ground_z, height_m and crown_radius_m, "
		         "in any order). The ETL column contract changed; the tree-points layer is skipped."),
		    *FString::Join(Missing, TEXT(", ")));
		return EMantlePlaceTreePointsOutcome::HeaderUnrecognised;
	}

	// HPS-53: the frame the pointer states is read first, and read in full. All three strings are
	// compared verbatim against what this host places in — its own georeference's CRS, in metres —
	// and nothing about them is interpreted: a CRS written another way is a different CRS here,
	// because deciding two spellings are one frame is a derivation this host does not make.
	// Refused before any row is read, because no row could change the answer.
	const bool bFrameOwed = Frame.IsOwed();
	if (bFrameOwed)
	{
		const auto Shown = [](const FString& Value) {
			return Value.IsEmpty() ? FString(TEXT("(not stated)")) : FString::Printf(TEXT("\"%s\""), *Value);
		};
		if (Frame.Crs.IsEmpty() || Frame.Units.IsEmpty() || Frame.HorizontalUnits.IsEmpty())
		{
			OutError = FString::Printf(
			    TEXT("The tree-point file does not state its frame in full: the manifest's Unreal block gives crs %s, "
			         "units %s and horizontal_units %s, and this manifest version requires all three. An "
			         "unstated frame is never assumed to be this host's metric UTM frame."),
			    *Shown(Frame.Crs), *Shown(Frame.Units), *Shown(Frame.HorizontalUnits));
			return EMantlePlaceTreePointsOutcome::Unplaceable;
		}
		if (Frame.HostCrs.IsEmpty() || !Frame.Crs.Equals(Frame.HostCrs, ESearchCase::CaseSensitive))
		{
			OutError = FString::Printf(
			    TEXT("The tree-point file's stated CRS is %s, which is not this host's frame (%s, the Unreal "
			         "block's georeference). Unreal places metric UTM coordinates only, and a file stated on "
			         "another grid is refused rather than reprojected."),
			    *Shown(Frame.Crs), *Shown(Frame.HostCrs));
			return EMantlePlaceTreePointsOutcome::Unplaceable;
		}
		if (!Frame.Units.Equals(TEXT("m"), ESearchCase::CaseSensitive)
		    || !Frame.HorizontalUnits.Equals(TEXT("m"), ESearchCase::CaseSensitive))
		{
			OutError = FString::Printf(
			    TEXT("The tree-point file's stated unit is not metres (units %s, horizontal_units %s). Unreal places "
			         "metric coordinates only, and a file stated in another unit is refused rather than scaled."),
			    *Shown(Frame.Units), *Shown(Frame.HorizontalUnits));
			return EMantlePlaceTreePointsOutcome::Unplaceable;
		}
	}

	// HPS-53's backstop: the landscape extent this block publishes. With none published there is
	// nothing to hold the file against, which is no evidence either way — so a stated frame that
	// matched stands, and an unstated one is refused, because it is never assumed to match.
	const bool bHasExtent = LandscapeSpanUeCm.X > 0.0 && LandscapeSpanUeCm.Y > 0.0;
	if (!bHasExtent && !bFrameOwed)
	{
		OutError = TEXT("The tree-point file states no CRS and no unit, and the manifest's Unreal block publishes "
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
		// Metres -> the engine's centimetres: the unit change Position already makes, so a scatter
		// reads a size in the unit it places in. A unit, not a derived value.
		Row.HeightCm = Row.HeightM * 100.0f;
		Row.CrownRadiusCm = Row.CrownRadiusM * 100.0f;
		Row.GroundZM = static_cast<float>(GroundZM);
		Row.bGroundKnown = !GroundField.IsEmpty();
		if (bReadFoliageType && FoliageColumn < Fields.Num())
		{
			Row.FoliageType = ReadFoliageType(Fields[FoliageColumn]);
		}
		OutRows.Add(Row);

		// The landscape is centred on the origin, so its extent in this frame is +/- half its span.
		// A comparison of two published values, in the units they were published in.
		if (bHasExtent
		    && (FMath::Abs(Row.Position.X) > HalfSpanNorthCm || FMath::Abs(Row.Position.Y) > HalfSpanEastCm))
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
		    TEXT("The tree-point file is not in this host's frame: %d of %d point(s) fall outside the landscape "
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
		    TEXT("The tree-point file parsed %d row(s) but the manifest declares point_count %d. The "
		         "payload and the manifest disagree; refusing to import a subset of the layer."),
		    OutRows.Num(), DeclaredPointCount);
		OutRows.Reset();
		return EMantlePlaceTreePointsOutcome::CountMismatch;
	}
	return EMantlePlaceTreePointsOutcome::Parsed;
}

EMantlePlaceFoliageType FMantlePlaceTreePointsLogic::ReadFoliageType(const FString& Cell)
{
	// Exact and case-sensitive, trimmed only of the CSV's own padding: the vocabulary's values are the
	// platform's words, and a case-fold or a prefix match invents members it never published.
	if (Cell.TrimStartAndEnd().Equals(TEXT("shrub"), ESearchCase::CaseSensitive))
	{
		return EMantlePlaceFoliageType::Shrub;
	}
	return EMantlePlaceFoliageType::Tree;
}

const TArray<FName>& FMantlePlaceTreePointsLogic::ColumnContract()
{
	static const TArray<FName> Columns = {
	    FName(TEXT("Position")),     FName(TEXT("FoliageType")),   FName(TEXT("HeightM")),
	    FName(TEXT("HeightCm")),     FName(TEXT("CrownRadiusM")),  FName(TEXT("CrownRadiusCm")),
	    FName(TEXT("GroundZM")),     FName(TEXT("bGroundKnown"))};
	return Columns;
}

namespace
{
const FMantlePlaceTreeLevelEntry* EntryFor(const FMantlePlaceFoliagePointsBlock& Block, EMantlePlaceTreeLevel Level)
{
	switch (Level)
	{
	case EMantlePlaceTreeLevel::Max: return &Block.LevelMax;
	case EMantlePlaceTreeLevel::Med: return &Block.LevelMed;
	case EMantlePlaceTreeLevel::Min: return &Block.LevelMin;
	}
	return nullptr;
}

bool LevelFromName(const FString& Name, EMantlePlaceTreeLevel& Out)
{
	if (Name == TEXT("MAX")) { Out = EMantlePlaceTreeLevel::Max; return true; }
	if (Name == TEXT("MED")) { Out = EMantlePlaceTreeLevel::Med; return true; }
	if (Name == TEXT("MIN")) { Out = EMantlePlaceTreeLevel::Min; return true; }
	return false; // RAW is never imported, and anything else is not a level this build knows
}

FMantlePlaceResolvedTreeLevel MaxLevel(const FMantlePlaceFoliagePointsBlock& Block)
{
	FMantlePlaceResolvedTreeLevel Out;
	Out.Applied = EMantlePlaceTreeLevel::Max;
	Out.Path = Block.Path;
	Out.Sha256 = Block.Sha256;
	Out.DeclaredCount = Block.Count;
	return Out;
}
}

FMantlePlaceResolvedTreeLevel FMantlePlaceTreePointsLogic::ResolveLevel(
    const FMantlePlaceFoliagePointsBlock& Block, EMantlePlaceTreeLevel Requested)
{
	FMantlePlaceResolvedTreeLevel Out = MaxLevel(Block);
	if (Requested == EMantlePlaceTreeLevel::Max)
	{
		return Out;
	}

	const FMantlePlaceTreeLevelEntry* Entry = EntryFor(Block, Requested);
	EMantlePlaceTreeLevel Applied = Requested;
	if (Entry != nullptr && Entry->Form == EMantlePlaceTreeLevelForm::SameAs)
	{
		EMantlePlaceTreeLevel Target;
		const FMantlePlaceTreeLevelEntry* TargetEntry = LevelFromName(Entry->SameAs, Target) ? EntryFor(Block, Target) : nullptr;
		if (TargetEntry == nullptr || TargetEntry->Form == EMantlePlaceTreeLevelForm::SameAs)
		{
			Out.FallbackReason = TEXT("the level names another that is unknown or itself the same as a third");
			return Out;
		}
		Applied = Target;
		Entry = TargetEntry;
		if (Target == EMantlePlaceTreeLevel::Max)
		{
			Out.Applied = EMantlePlaceTreeLevel::Max;
			return Out;
		}
	}

	if (Entry != nullptr && Entry->Form == EMantlePlaceTreeLevelForm::Pointer && !Entry->Path.IsEmpty())
	{
		Out.Applied = Applied;
		Out.Path = Entry->Path;
		Out.Sha256 = Entry->Sha256;
		Out.DeclaredCount = Entry->Count;
		Out.bOwnFrame = !Entry->Crs.IsEmpty() || !Entry->Units.IsEmpty() || !Entry->HorizontalUnits.IsEmpty();
		Out.Crs = Entry->Crs;
		Out.Units = Entry->Units;
		Out.HorizontalUnits = Entry->HorizontalUnits;
		return Out;
	}
	if (Entry != nullptr && Entry->Form == EMantlePlaceTreeLevelForm::RowCut && Entry->Rows > 0)
	{
		Out.Applied = Applied;
		Out.RowLimit = Entry->Rows;
		// The cut keeps the MAX file's first rows, so the file cannot hold fewer than the lesser of
		// the cut and what MAX holds. Unknown MAX count = unknown cut count.
		Out.DeclaredCount = Block.Count > 0 ? FMath::Min(Entry->Rows, Block.Count) : 0;
		return Out;
	}

	Out.FallbackReason = TEXT("the bundle does not publish that level");
	return Out;
}

FString FMantlePlaceTreePointsLogic::CutRows(const FString& CsvText, int32 Rows)
{
	if (Rows <= 0)
	{
		return CsvText;
	}
	// Walk the lines, counting the header as line one and skipping blank lines, and cut after the
	// Rows-th data line. Ending the text at the line's end keeps its own newline out, which the
	// parser does not need.
	int32 Pos = 0;
	int32 Lines = 0;
	const int32 Len = CsvText.Len();
	while (Pos < Len)
	{
		int32 End = Pos;
		while (End < Len && CsvText[End] != TEXT('\n'))
		{
			++End;
		}
		int32 TextEnd = End;
		if (TextEnd > Pos && CsvText[TextEnd - 1] == TEXT('\r'))
		{
			--TextEnd;
		}
		if (TextEnd > Pos)
		{
			++Lines;
			if (Lines == Rows + 1)
			{
				return CsvText.Left(TextEnd);
			}
		}
		Pos = End + 1;
	}
	return CsvText;
}
