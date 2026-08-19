// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceTreePointsLogic.h"

bool FMantlePlaceTreePointsLogic::ParseCsv(
    const FString& CsvText,
    double OriginEastingM,
    double OriginNorthingM,
    TArray<FMantlePlaceTreePointRow>& OutRows,
    FString& OutError)
{
	OutRows.Reset();

	TArray<FString> Lines;
	CsvText.ParseIntoArrayLines(Lines, /*bCullEmpty*/ true);
	if (Lines.Num() == 0 || !Lines[0].TrimStartAndEnd().Equals(TEXT("x,y,ground_z,height_m,crown_radius_m")))
	{
		OutError = TEXT("TreePoints.csv header is missing or not the expected "
		                "\"x,y,ground_z,height_m,crown_radius_m\" (ETL column contract changed?).");
		return false;
	}

	OutRows.Reserve(Lines.Num() - 1);
	for (int32 LineIndex = 1; LineIndex < Lines.Num(); ++LineIndex)
	{
		TArray<FString> Fields;
		Lines[LineIndex].ParseIntoArray(Fields, TEXT(","), /*bCullEmpty*/ false);
		if (Fields.Num() != 5 || !Fields[0].IsNumeric() || !Fields[1].IsNumeric())
		{
			continue; // one malformed row must not drop the whole layer
		}

		const double UtmX = FCString::Atod(*Fields[0]);
		const double UtmY = FCString::Atod(*Fields[1]);
		// ground_z is deliberately empty when the DEM had no data under the point.
		const double GroundZM = Fields[2].IsEmpty() ? 0.0 : FCString::Atod(*Fields[2]);

		FMantlePlaceTreePointRow Row;
		// Same frame math as the drape/mesh placement: East -> +X, North -> +Y, orthometric m -> +Z cm.
		Row.Position = FVector(
		    (UtmX - OriginEastingM) * 100.0,
		    (UtmY - OriginNorthingM) * 100.0,
		    GroundZM * 100.0);
		Row.HeightM = FCString::Atof(*Fields[3]);
		Row.CrownRadiusM = FCString::Atof(*Fields[4]);
		Row.GroundZM = static_cast<float>(GroundZM);
		OutRows.Add(Row);
	}
	return true;
}
