// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceRoadSplinesGpkg.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "SQLiteDatabase.h"           // SQLiteCore (engine plugin): FSQLiteDatabase
#include "SQLitePreparedStatement.h"  // SQLiteCore: FSQLitePreparedStatement

namespace
{
/** Bounds-checked little/big-endian reads over a byte range. Every read says whether it fit. */
struct FByteReader
{
	const uint8* Data = nullptr;
	int64 Num = 0;
	int64 Pos = 0;
	bool bLittle = true;

	int64 Remaining() const { return Num - Pos; }

	bool ReadU8(uint8& Out)
	{
		if (Remaining() < 1)
		{
			return false;
		}
		Out = Data[Pos++];
		return true;
	}

	bool ReadU32(uint32& Out)
	{
		if (Remaining() < 4)
		{
			return false;
		}
		uint32 Value = 0;
		for (int32 Index = 0; Index < 4; ++Index)
		{
			const uint32 Byte = Data[Pos + (bLittle ? Index : 3 - Index)];
			Value |= Byte << (8 * Index);
		}
		Pos += 4;
		Out = Value;
		return true;
	}

	bool ReadDouble(double& Out)
	{
		if (Remaining() < 8)
		{
			return false;
		}
		uint64 Bits = 0;
		for (int32 Index = 0; Index < 8; ++Index)
		{
			const uint64 Byte = Data[Pos + (bLittle ? Index : 7 - Index)];
			Bits |= Byte << (8 * Index);
		}
		Pos += 8;
		FMemory::Memcpy(&Out, &Bits, sizeof(double));
		return true;
	}

	bool Skip(int64 Bytes)
	{
		if (Bytes < 0 || Remaining() < Bytes)
		{
			return false;
		}
		Pos += Bytes;
		return true;
	}
};

// WKB geometry type codes (the OGC base type, after the dimension is split off).
constexpr uint32 WkbLineString = 2;
constexpr uint32 WkbMultiLineString = 5;

/**
 * One WKB geometry: byte-order byte, type, body. Reads LineString and MultiLineString, ISO
 * (1002, 2002, 3002) or extended (flag bits) alike. Any other type leaves OutIsLine false and
 * consumes nothing past its header: the caller skips the feature. OutIsLine is true only when the
 * geometry was a line type and its parts were appended.
 */
bool DecodeWkb(FByteReader& Reader, TArray<TArray<FVector>>& OutParts, bool& OutIsLine, FString& OutError)
{
	OutIsLine = false;
	uint8 ByteOrder = 0;
	uint32 RawType = 0;
	if (!Reader.ReadU8(ByteOrder) || ByteOrder > 1)
	{
		OutError = TEXT("geometry has no valid byte-order marker");
		return false;
	}
	Reader.bLittle = (ByteOrder == 1);
	if (!Reader.ReadU32(RawType))
	{
		OutError = TEXT("geometry is cut off before its type");
		return false;
	}

	bool bHasZ = (RawType & 0x80000000u) != 0;
	bool bHasM = (RawType & 0x40000000u) != 0;
	const bool bHasSrid = (RawType & 0x20000000u) != 0;
	uint32 Type = RawType & 0x1FFFFFFFu;
	if (Type >= 1000)
	{
		const uint32 Dimension = Type / 1000; // ISO: 1 = Z, 2 = M, 3 = ZM
		if (Dimension > 3)
		{
			OutError = FString::Printf(TEXT("geometry type %u is not a WKB type"), RawType);
			return false;
		}
		bHasZ = bHasZ || Dimension == 1 || Dimension == 3;
		bHasM = bHasM || Dimension == 2 || Dimension == 3;
		Type %= 1000;
	}
	if (bHasSrid && !Reader.Skip(4))
	{
		OutError = TEXT("geometry is cut off in its SRID");
		return false;
	}

	if (Type == WkbLineString)
	{
		uint32 PointCount = 0;
		if (!Reader.ReadU32(PointCount))
		{
			OutError = TEXT("LineString is cut off before its point count");
			return false;
		}
		const int64 Stride = 8 * (2 + (bHasZ ? 1 : 0) + (bHasM ? 1 : 0));
		if (static_cast<int64>(PointCount) > Reader.Remaining() / Stride)
		{
			OutError = FString::Printf(TEXT("LineString states %u points and is cut off before them"), PointCount);
			return false;
		}
		TArray<FVector>& Part = OutParts.AddDefaulted_GetRef();
		Part.Reserve(static_cast<int32>(PointCount));
		for (uint32 Index = 0; Index < PointCount; ++Index)
		{
			double Lon = 0.0, Lat = 0.0, Z = 0.0, Measure = 0.0;
			Reader.ReadDouble(Lon);
			Reader.ReadDouble(Lat);
			if (bHasZ)
			{
				Reader.ReadDouble(Z);
			}
			if (bHasM)
			{
				Reader.ReadDouble(Measure);
			}
			Part.Add(FVector(Lon, Lat, Z));
		}
		OutIsLine = true;
		return true;
	}

	if (Type == WkbMultiLineString)
	{
		uint32 MemberCount = 0;
		if (!Reader.ReadU32(MemberCount))
		{
			OutError = TEXT("MultiLineString is cut off before its member count");
			return false;
		}
		constexpr int64 SmallestMember = 9; // byte order + type
		if (static_cast<int64>(MemberCount) > Reader.Remaining() / SmallestMember)
		{
			OutError = FString::Printf(TEXT("MultiLineString states %u members and is cut off before them"), MemberCount);
			return false;
		}
		for (uint32 Index = 0; Index < MemberCount; ++Index)
		{
			bool bMemberIsLine = false;
			if (!DecodeWkb(Reader, OutParts, bMemberIsLine, OutError))
			{
				return false;
			}
			if (!bMemberIsLine)
			{
				OutError = TEXT("a MultiLineString member is not a LineString");
				return false;
			}
		}
		OutIsLine = true;
		return true;
	}

	return true; // a point, polygon or collection: not a road centreline, skipped by the caller
}

/** A feature table named by gpkg_contents, with the geometry column and SRS gpkg_geometry_columns gives it. */
struct FFeatureTable
{
	FString Name;
	FString GeometryColumn;
	int32 SrsId = 0;
};

FString QuoteIdentifier(const FString& Name)
{
	return TEXT("\"") + Name.Replace(TEXT("\""), TEXT("\"\"")) + TEXT("\"");
}

bool ListFeatureTables(FSQLiteDatabase& Database, TArray<FFeatureTable>& OutTables, FString& OutError)
{
	FSQLitePreparedStatement Query = Database.PrepareStatement(
	    TEXT("SELECT c.table_name, g.column_name, g.srs_id FROM gpkg_contents c "
	         "JOIN gpkg_geometry_columns g ON g.table_name = c.table_name "
	         "WHERE c.data_type = 'features' ORDER BY c.table_name"));
	if (!Query.IsValid())
	{
		OutError = TEXT("the file is not a GeoPackage (it has no gpkg_contents and gpkg_geometry_columns tables).");
		return false;
	}
	for (;;)
	{
		const ESQLitePreparedStatementStepResult Step = Query.Step();
		if (Step == ESQLitePreparedStatementStepResult::Done)
		{
			return true;
		}
		if (Step != ESQLitePreparedStatementStepResult::Row)
		{
			OutError = TEXT("the GeoPackage's table list could not be read.");
			return false;
		}
		FFeatureTable& Table = OutTables.AddDefaulted_GetRef();
		Query.GetColumnValueByIndex(0, Table.Name);
		Query.GetColumnValueByIndex(1, Table.GeometryColumn);
		Query.GetColumnValueByIndex(2, Table.SrsId);
	}
}

bool ReadDatabase(
    FSQLiteDatabase& Database,
    const TArray<FString>& RequestedTables,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen,
    int32& OutRowsUnreadable,
    FString& OutError)
{
	TArray<FFeatureTable> Available;
	if (!ListFeatureTables(Database, Available, OutError))
	{
		return false;
	}

	TArray<FFeatureTable> Chosen;
	if (RequestedTables.IsEmpty())
	{
		// A row that lists no `tables` holds exactly one (format.md 6.3): that one is read whatever
		// the producer named it, and a file of several is refused, never read as any one of them.
		if (Available.Num() > 1)
		{
			TArray<FString> Names;
			for (const FFeatureTable& Table : Available)
			{
				Names.Add(Table.Name);
			}
			OutError = FString::Printf(
			    TEXT("the manifest lists no tables, so the GeoPackage must hold exactly one feature table, and it holds %d (%s)."),
			    Available.Num(), *FString::Join(Names, TEXT(", ")));
			return false;
		}
		Chosen = Available;
	}
	else
	{
		for (const FString& Requested : RequestedTables)
		{
			const FFeatureTable* Found = Available.FindByPredicate(
			    [&Requested](const FFeatureTable& Table) { return Table.Name.Equals(Requested, ESearchCase::IgnoreCase); });
			if (Found == nullptr)
			{
				OutError = FString::Printf(TEXT("the manifest names the table \"%s\" and the GeoPackage holds no such feature table."), *Requested);
				return false;
			}
			Chosen.Add(*Found);
		}
	}
	if (Chosen.IsEmpty())
	{
		OutError = TEXT("the GeoPackage holds no feature table.");
		return false;
	}

	// The roads are placed only from the frame this plugin projects. Another is a named refusal,
	// never a reprojection (HPS-33), and it is checked for every table before any road is read.
	for (const FFeatureTable& Table : Chosen)
	{
		if (Table.SrsId != FMantlePlaceRoadSplinesGpkg::Wgs84SrsId)
		{
			OutError = FString::Printf(
			    TEXT("table \"%s\" is in EPSG:%d, not EPSG:%d (WGS84 lon/lat), and this plugin projects no other "
			         "geographic frame, so the roads were not placed."),
			    *Table.Name, Table.SrsId, FMantlePlaceRoadSplinesGpkg::Wgs84SrsId);
			return false;
		}
	}

	for (const FFeatureTable& Table : Chosen)
	{
		FSQLitePreparedStatement Rows = Database.PrepareStatement(
		    *FString::Printf(TEXT("SELECT * FROM %s"), *QuoteIdentifier(Table.Name)));
		if (!Rows.IsValid())
		{
			OutError = FString::Printf(TEXT("table \"%s\" could not be read."), *Table.Name);
			return false;
		}
		for (;;)
		{
			const ESQLitePreparedStatementStepResult Step = Rows.Step();
			if (Step == ESQLitePreparedStatementStepResult::Done)
			{
				break;
			}
			if (Step != ESQLitePreparedStatementStepResult::Row)
			{
				OutError = FString::Printf(TEXT("table \"%s\" stopped reading partway."), *Table.Name);
				return false;
			}

			TArray<uint8> Blob;
			if (!Rows.GetColumnValueByName(*Table.GeometryColumn, Blob) || Blob.IsEmpty())
			{
				continue; // a feature with no geometry has no road to place
			}
			TArray<TArray<FVector>> Parts;
			bool bIsLine = false;
			FString GeometryError;
			if (!FMantlePlaceRoadSplinesGpkg::DecodeGeometry(Blob, Parts, bIsLine, GeometryError))
			{
				++OutRowsUnreadable; // one bad feature must not drop the whole layer
				continue;
			}
			if (!bIsLine)
			{
				continue; // not a road centreline
			}

			FMantlePlaceRoadSpline Prototype;
			Rows.GetColumnValueByName(TEXT("width_m_estimated"), Prototype.WidthMEstimated);
			Rows.GetColumnValueByName(TEXT("class"), Prototype.RoadClass);
			Rows.GetColumnValueByName(TEXT("name"), Prototype.Name);
			for (const TArray<FVector>& Part : Parts)
			{
				FMantlePlaceRoadSplinesLogic::AppendLonLatLine(
				    Part, Prototype, OriginEastingM, OriginNorthingM, Epsg, OutSplines, OutLinesSeen);
			}
		}
	}
	return true;
}
}

bool FMantlePlaceRoadSplinesGpkg::DecodeGeometry(
    const TArray<uint8>& Blob,
    TArray<TArray<FVector>>& OutParts,
    bool& OutIsLine,
    FString& OutError)
{
	OutParts.Reset();
	OutIsLine = false;

	// The GeoPackage geometry header: "GP", version, flags, srs_id, then an optional envelope.
	if (Blob.Num() < 8 || Blob[0] != 'G' || Blob[1] != 'P')
	{
		OutError = TEXT("geometry has no GeoPackage header.");
		return false;
	}
	const uint8 Flags = Blob[3];
	if ((Flags & 0x20) != 0)
	{
		OutError = TEXT("geometry uses the GeoPackage extended binary form, which this reader does not read.");
		return false;
	}
	if ((Flags & 0x10) != 0)
	{
		return true; // an empty geometry: nothing to place, and not a fault
	}
	int64 EnvelopeBytes = 0;
	switch ((Flags >> 1) & 0x07)
	{
	case 0: EnvelopeBytes = 0; break;
	case 1: EnvelopeBytes = 32; break;
	case 2:
	case 3: EnvelopeBytes = 48; break;
	case 4: EnvelopeBytes = 64; break;
	default:
		OutError = TEXT("geometry states an envelope this reader does not know.");
		return false;
	}

	FByteReader Reader;
	Reader.Data = Blob.GetData();
	Reader.Num = Blob.Num();
	Reader.Pos = 8;
	if (!Reader.Skip(EnvelopeBytes))
	{
		OutError = TEXT("geometry is cut off inside its envelope.");
		return false;
	}
	if (!DecodeWkb(Reader, OutParts, OutIsLine, OutError))
	{
		OutParts.Reset();
		OutIsLine = false;
		return false;
	}
	return true;
}

bool FMantlePlaceRoadSplinesGpkg::ReadFile(
    const FString& GpkgFilePath,
    const TArray<FString>& Tables,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen,
    int32& OutRowsUnreadable,
    FString& OutError)
{
	OutSplines.Reset();
	OutLinesSeen = 0;
	OutRowsUnreadable = 0;

	if (!FMantlePlaceRoadSplinesLogic::IsUtmEpsg(Epsg))
	{
		OutError = FString::Printf(
		    TEXT("the origin's EPSG:%d is not a UTM zone (32601-32660 or 32701-32760), so road centerlines "
		         "cannot be projected into the frame; the whole layer is refused rather than dropped point by point."),
		    Epsg);
		return false;
	}

	// The database, and every statement opened on it, lives inside this scope: the file is closed
	// before the caller can delete it.
	FSQLiteDatabase Database;
	if (!Database.Open(*GpkgFilePath, ESQLiteDatabaseOpenMode::ReadOnly))
	{
		OutError = TEXT("the file could not be opened as a SQLite database.");
		return false;
	}
	const bool bRead = ReadDatabase(
	    Database, Tables, OriginEastingM, OriginNorthingM, Epsg, OutSplines, OutLinesSeen, OutRowsUnreadable, OutError);
	Database.Close();
	if (!bRead)
	{
		OutSplines.Reset();
		OutLinesSeen = 0;
	}
	return bRead;
}

bool FMantlePlaceRoadSplinesGpkg::ReadBytes(
    const TArray<uint8>& GpkgBytes,
    const TArray<FString>& Tables,
    double OriginEastingM,
    double OriginNorthingM,
    int32 Epsg,
    TArray<FMantlePlaceRoadSpline>& OutSplines,
    int32& OutLinesSeen,
    int32& OutRowsUnreadable,
    FString& OutError)
{
	const FString ScratchPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
	    FPaths::ProjectIntermediateDir(), TEXT("MantlePlace"),
	    FString::Printf(TEXT("RoadSplines-%s.gpkg"), *FGuid::NewGuid().ToString(EGuidFormats::Digits))));
	ON_SCOPE_EXIT
	{
		IFileManager::Get().Delete(*ScratchPath, /*RequireExists*/ false, /*EvenReadOnly*/ true, /*Quiet*/ true);
	};
	if (!FFileHelper::SaveArrayToFile(GpkgBytes, *ScratchPath))
	{
		OutSplines.Reset();
		OutLinesSeen = 0;
		OutRowsUnreadable = 0;
		OutError = FString::Printf(TEXT("the GeoPackage could not be written to %s to be read."), *ScratchPath);
		return false;
	}
	return ReadFile(ScratchPath, Tables, OriginEastingM, OriginNorthingM, Epsg, OutSplines, OutLinesSeen, OutRowsUnreadable, OutError);
}
