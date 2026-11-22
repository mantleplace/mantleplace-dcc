// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceTreePointsLogic.h"

#include "MantlePlaceImportManifest.h"

EMantlePlaceTreePointsOutcome FMantlePlaceTreePointsLogic::ParseCsv(
    const FString& CsvText,
    double OriginEastingM,
    double OriginNorthingM,
    const FMantlePlaceTreePointsFrame& Frame,
    const FVector2D& LandscapeSpanUeCm,
    int32 DeclaredPointCount,
    TArray<FMantlePlaceTreePointRow>& OutRows,
    FString& OutError,
    const FString& FoliageTypeVocabulary,
    FString* OutNote)
{
	OutRows.Reset();
	if (OutNote != nullptr)
	{
		OutNote->Reset();
	}

	TArray<FString> Lines;
	CsvText.ParseIntoArrayLines(Lines, true);
	if (Lines.Num() == 0)
	{
		OutError = TEXT("The tree-point file is empty: it has no header row naming the columns x, y, ground_z, "
		                "height_m and crown_radius_m.");
		return EMantlePlaceTreePointsOutcome::HeaderUnrecognised;
	}

	static const TCHAR* const RequiredColumns[] = {
	    TEXT("x"), TEXT("y"), TEXT("ground_z"), TEXT("height_m"), TEXT("crown_radius_m")};
	enum : int32 { ColumnX, ColumnY, ColumnGroundZ, ColumnHeight, ColumnCrown, RequiredColumnCount };

	TArray<FString> HeaderFields;
	Lines[0].ParseIntoArray(HeaderFields, TEXT(","), false);
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
	const double EdgeToleranceCm = EdgeToleranceCmValue;
	int32 OutsideCount = 0;
	int32 TotalRows = 0;
	FString FirstOutsideX, FirstOutsideY;

	OutRows.Reserve(Lines.Num() - 1);
	for (int32 LineIndex = 1; LineIndex < Lines.Num(); ++LineIndex)
	{
		TArray<FString> Fields;
		Lines[LineIndex].ParseIntoArray(Fields, TEXT(","), false);
		if (Fields.Num() <= WidestIndex || !Fields[ColumnIndex[ColumnX]].IsNumeric()
		    || !Fields[ColumnIndex[ColumnY]].IsNumeric())
		{
			continue;
		}

		const double UtmX = FCString::Atod(*Fields[ColumnIndex[ColumnX]]);
		const double UtmY = FCString::Atod(*Fields[ColumnIndex[ColumnY]]);
		const FString& GroundField = Fields[ColumnIndex[ColumnGroundZ]];
		const double GroundZM = GroundField.IsEmpty() ? 0.0 : FCString::Atod(*GroundField);

		FMantlePlaceTreePointRow Row;
		Row.Position = FMantlePlaceVaultManifest::ProjectedToUeCm(
		    UtmX - OriginEastingM, UtmY - OriginNorthingM, GroundZM);
		Row.HeightM = FCString::Atof(*Fields[ColumnIndex[ColumnHeight]]);
		Row.CrownRadiusM = FCString::Atof(*Fields[ColumnIndex[ColumnCrown]]);
		Row.HeightCm = Row.HeightM * 100.0f;
		Row.CrownRadiusCm = Row.CrownRadiusM * 100.0f;
		Row.GroundZM = static_cast<float>(GroundZM);
		Row.bGroundKnown = !GroundField.IsEmpty();
		if (bReadFoliageType && FoliageColumn < Fields.Num())
		{
			Row.FoliageType = ReadFoliageType(Fields[FoliageColumn]);
		}
		++TotalRows;

		if (bHasExtent
		    && (FMath::Abs(Row.Position.X) > HalfSpanNorthCm + EdgeToleranceCm
		        || FMath::Abs(Row.Position.Y) > HalfSpanEastCm + EdgeToleranceCm))
		{
			if (OutsideCount++ == 0)
			{
				FirstOutsideX = Fields[ColumnIndex[ColumnX]];
				FirstOutsideY = Fields[ColumnIndex[ColumnY]];
			}
			continue;
		}
		OutRows.Add(Row);
	}

	if (OutsideCount > 0)
	{
		if (static_cast<int64>(OutsideCount) * 100 > static_cast<int64>(TotalRows) * MaxOutsideSharePercentValue)
		{
			OutError = FString::Printf(
			    TEXT("The tree-point file is not in this host's frame: %d of %d point(s) fall outside the "
			         "landscape extent the manifest publishes (%.1f m east-west by %.1f m north-south, centred "
			         "on the origin); the first is x=%s, y=%s. Unreal places metric UTM coordinates only, and "
			         "a file stated on another grid or in another unit is refused rather than converted."),
			    OutsideCount, TotalRows, LandscapeSpanUeCm.Y / 100.0, LandscapeSpanUeCm.X / 100.0,
			    *FirstOutsideX, *FirstOutsideY);
			OutRows.Reset();
			return EMantlePlaceTreePointsOutcome::Unplaceable;
		}
		if (OutNote != nullptr)
		{
			*OutNote = FString::Printf(
			    TEXT("%d of %d point(s) fall outside the landscape extent the manifest publishes (%.1f m "
			         "east-west by %.1f m north-south, centred on the origin) and were left out; the first "
			         "is x=%s, y=%s. The other %d are inside it and were placed."),
			    OutsideCount, TotalRows, LandscapeSpanUeCm.Y / 100.0, LandscapeSpanUeCm.X / 100.0,
			    *FirstOutsideX, *FirstOutsideY, OutRows.Num());
		}
	}

	if (DeclaredPointCount > 0 && TotalRows != DeclaredPointCount)
	{
		OutError = FString::Printf(
		    TEXT("The tree-point file parsed %d row(s) but the manifest declares point_count %d. The "
		         "payload and the manifest disagree; refusing to import a subset of the layer."),
		    TotalRows, DeclaredPointCount);
		OutRows.Reset();
		if (OutNote != nullptr)
		{
			OutNote->Reset();
		}
		return EMantlePlaceTreePointsOutcome::CountMismatch;
	}
	return EMantlePlaceTreePointsOutcome::Parsed;
}

EMantlePlaceFoliageType FMantlePlaceTreePointsLogic::ReadFoliageType(const FString& Cell)
{
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
	return false;
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
