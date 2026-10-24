// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceRoadSplinesGpkg.h"
#include "MantlePlaceRoadSplinesLogic.h"

#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "SQLiteDatabase.h"
#include "SQLitePreparedStatement.h"

// The road splines read from the layer's GeoPackage. Headless: the GeoPackage is built here, row by
// row, through the same engine SQLite module the reader uses, so nothing in this test is a binary
// fixture. The geometry blobs are written byte by byte, which is what makes the reader's
// decode a thing this test can break: the ISO and extended type codes, both byte orders, the
// envelope, and a blob cut off partway.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceRoadSplinesGpkgTest,
    "MantlePlace.Import.RoadSplinesGpkg",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace MantlePlaceRoadSplinesGpkgTestSupport
{
void PutU32(TArray<uint8>& Out, uint32 Value, bool bLittle)
{
	for (int32 Index = 0; Index < 4; ++Index)
	{
		Out.Add(static_cast<uint8>((Value >> (8 * (bLittle ? Index : 3 - Index))) & 0xFF));
	}
}

void PutDouble(TArray<uint8>& Out, double Value, bool bLittle)
{
	uint64 Bits = 0;
	FMemory::Memcpy(&Bits, &Value, sizeof(double));
	for (int32 Index = 0; Index < 8; ++Index)
	{
		Out.Add(static_cast<uint8>((Bits >> (8 * (bLittle ? Index : 7 - Index))) & 0xFF));
	}
}

/** One WKB LineString. TypeCode is written as given, so a test picks ISO (1002) or plain (2). */
void PutLineString(TArray<uint8>& Out, const TArray<FVector>& Points, uint32 TypeCode, bool bWithZ, bool bLittle)
{
	Out.Add(bLittle ? 1 : 0);
	PutU32(Out, TypeCode, bLittle);
	PutU32(Out, static_cast<uint32>(Points.Num()), bLittle);
	for (const FVector& Point : Points)
	{
		PutDouble(Out, Point.X, bLittle);
		PutDouble(Out, Point.Y, bLittle);
		if (bWithZ)
		{
			PutDouble(Out, Point.Z, bLittle);
		}
	}
}

/** The GeoPackage geometry header: GP, version 0, flags, srs_id, then the envelope bytes asked for. */
TArray<uint8> Header(bool bLittle, int32 EnvelopeIndicator, bool bEmpty, int32 SrsId = 4326)
{
	TArray<uint8> Out;
	Out.Add('G');
	Out.Add('P');
	Out.Add(0);
	Out.Add(static_cast<uint8>((bLittle ? 1 : 0) | (EnvelopeIndicator << 1) | (bEmpty ? 0x10 : 0)));
	PutU32(Out, static_cast<uint32>(SrsId), bLittle);
	static const int32 EnvelopeBytes[] = { 0, 32, 48, 48, 64 };
	Out.AddZeroed(EnvelopeBytes[EnvelopeIndicator]);
	return Out;
}

TArray<uint8> LineBlob(const TArray<FVector>& Points, bool bLittle = true, bool bIso = true, bool bWithZ = true)
{
	TArray<uint8> Blob = Header(bLittle, 0, false);
	PutLineString(Blob, Points, bIso && bWithZ ? 1002u : 2u, bWithZ, bLittle);
	return Blob;
}

TArray<uint8> MultiLineBlob(const TArray<TArray<FVector>>& Parts)
{
	TArray<uint8> Blob = Header(true, 0, false);
	Blob.Add(1);
	PutU32(Blob, 1005u, true);
	PutU32(Blob, static_cast<uint32>(Parts.Num()), true);
	for (const TArray<FVector>& Part : Parts)
	{
		PutLineString(Blob, Part, 1002u, true, true);
	}
	return Blob;
}

TArray<uint8> PointBlob(double Lon, double Lat)
{
	TArray<uint8> Blob = Header(true, 0, false);
	Blob.Add(1);
	PutU32(Blob, 1u, true);
	PutDouble(Blob, Lon, true);
	PutDouble(Blob, Lat, true);
	return Blob;
}

bool Exec(FSQLiteDatabase& Database, const TCHAR* Statement)
{
	return Database.Execute(Statement);
}

struct FRoadRow
{
	TArray<uint8> Geometry; // empty = a NULL geometry
	FString RoadClass;
	FString Name;
	double Width = 0.0;
};

/** Build a synthetic GeoPackage at Path: one feature table of road rows, in the given SRS. */
bool BuildGpkg(const FString& Path, const FString& Table, int32 SrsId, const TArray<FRoadRow>& Rows)
{
	FSQLiteDatabase Database;
	if (!Database.Open(*Path, ESQLiteDatabaseOpenMode::ReadWriteCreate))
	{
		return false;
	}
	bool bOk = Exec(Database, TEXT("CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT, srs_id INTEGER)"))
		&& Exec(Database, TEXT("CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL)"))
		&& Exec(Database, *FString::Printf(TEXT("CREATE TABLE \"%s\" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom BLOB, \"class\" TEXT, name TEXT, width_m_estimated REAL)"), *Table))
		&& Exec(Database, *FString::Printf(TEXT("INSERT INTO gpkg_contents VALUES ('%s', 'features', '%s', %d)"), *Table, *Table, SrsId))
		&& Exec(Database, *FString::Printf(TEXT("INSERT INTO gpkg_geometry_columns VALUES ('%s', 'geom', 'LINESTRING', %d, 1, 0)"), *Table, SrsId));
	{
		FSQLitePreparedStatement Insert = Database.PrepareStatement(
		    *FString::Printf(TEXT("INSERT INTO \"%s\" (geom, \"class\", name, width_m_estimated) VALUES (?, ?, ?, ?)"), *Table));
		bOk = bOk && Insert.IsValid();
		for (const FRoadRow& Row : Rows)
		{
			if (!bOk)
			{
				break;
			}
			Insert.Reset();
			Insert.ClearBindings();
			if (Row.Geometry.IsEmpty())
			{
				bOk = Insert.SetBindingValueByIndex(1);
			}
			else
			{
				bOk = Insert.SetBindingValueByIndex(1, TArrayView<const uint8>(Row.Geometry));
			}
			bOk = bOk && Insert.SetBindingValueByIndex(2, Row.RoadClass)
				&& Insert.SetBindingValueByIndex(3, Row.Name)
				&& Insert.SetBindingValueByIndex(4, Row.Width)
				&& Insert.Execute();
		}
	}
	Database.Close();
	return bOk;
}

/** Add a second feature table to a GeoPackage BuildGpkg made: just what the reader lists tables from. */
bool AddFeatureTable(const FString& Path, const FString& Table, int32 SrsId)
{
	FSQLiteDatabase Database;
	if (!Database.Open(*Path, ESQLiteDatabaseOpenMode::ReadWrite))
	{
		return false;
	}
	const bool bOk = Exec(Database, *FString::Printf(TEXT("CREATE TABLE \"%s\" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom BLOB)"), *Table))
		&& Exec(Database, *FString::Printf(TEXT("INSERT INTO gpkg_contents VALUES ('%s', 'features', '%s', %d)"), *Table, *Table, SrsId))
		&& Exec(Database, *FString::Printf(TEXT("INSERT INTO gpkg_geometry_columns VALUES ('%s', 'geom', 'GEOMETRY', %d, 0, 0)"), *Table, SrsId));
	Database.Close();
	return bOk;
}

FString ScratchPath()
{
	return FPaths::ConvertRelativePathToFull(FPaths::Combine(
	    FPaths::ProjectIntermediateDir(), TEXT("MantlePlace"), TEXT("tests"),
	    FString::Printf(TEXT("RoadSplinesGpkgTest-%s.gpkg"), *FGuid::NewGuid().ToString(EGuidFormats::Digits))));
}
}

bool FMantlePlaceRoadSplinesGpkgTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceRoadSplinesGpkgTestSupport;
	using FGpkg = FMantlePlaceRoadSplinesGpkg;
	using FLogic = FMantlePlaceRoadSplinesLogic;

	// A UTM 13N origin at a known lon/lat, so the roads below land at known offsets.
	constexpr int32 Epsg = 32613;
	constexpr double OriginLon = -105.0;
	constexpr double OriginLat = 40.0;
	double OriginEastingM = 0.0, OriginNorthingM = 0.0;
	TestTrue(TEXT("the origin projects"), FLogic::LonLatToUtm(OriginLon, OriginLat, Epsg, OriginEastingM, OriginNorthingM));

	const TArray<FVector> Comet = { FVector(OriginLon, OriginLat, 2640.0), FVector(OriginLon + 0.001, OriginLat, 2641.0) };

	// --- DecodeGeometry: the blob, in every shape the ETL's writer or another could produce -------
	{
		TArray<TArray<FVector>> Parts;
		bool bIsLine = false;
		FString Error;

		TestTrue(TEXT("an ISO LineString Z decodes"), FGpkg::DecodeGeometry(LineBlob(Comet), Parts, bIsLine, Error));
		TestTrue(TEXT("and is a line"), bIsLine);
		TestEqual(TEXT("one part"), Parts.Num(), 1);
		if (Parts.Num() == 1 && Parts[0].Num() == 2)
		{
			TestEqual(TEXT("lon"), Parts[0][1].X, OriginLon + 0.001, 1e-12);
			TestEqual(TEXT("lat"), Parts[0][1].Y, OriginLat, 1e-12);
			TestEqual(TEXT("z"), Parts[0][1].Z, 2641.0, 1e-9);
		}

		// Big-endian, no Z, with a 32-byte envelope: z is 0, the envelope is stepped over.
		TArray<uint8> BigEndian = Header(false, 1, false);
		PutLineString(BigEndian, Comet, 2u, false, false);
		TestTrue(TEXT("a big-endian XY LineString with an envelope decodes"),
			FGpkg::DecodeGeometry(BigEndian, Parts, bIsLine, Error));
		TestTrue(TEXT("and is a line"), bIsLine);
		if (Parts.Num() == 1 && Parts[0].Num() == 2)
		{
			TestEqual(TEXT("big-endian lon"), Parts[0][1].X, OriginLon + 0.001, 1e-12);
			TestEqual(TEXT("no Z reads as 0"), Parts[0][1].Z, 0.0, 1e-12);
		}

		// The extended (EWKB) Z flag, 0x80000002, reads the same as ISO 1002.
		TArray<uint8> Extended = Header(true, 0, false);
		PutLineString(Extended, Comet, 0x80000002u, true, true);
		TestTrue(TEXT("an EWKB LineString Z decodes"), FGpkg::DecodeGeometry(Extended, Parts, bIsLine, Error));
		if (Parts.Num() == 1 && Parts[0].Num() == 2)
		{
			TestEqual(TEXT("EWKB z"), Parts[0][0].Z, 2640.0, 1e-9);
		}

		TestTrue(TEXT("a MultiLineString decodes"),
			FGpkg::DecodeGeometry(MultiLineBlob({ Comet, Comet }), Parts, bIsLine, Error));
		TestTrue(TEXT("and is a line"), bIsLine);
		TestEqual(TEXT("one part per member"), Parts.Num(), 2);

		// Not a road centreline: skipped, not a fault.
		TestTrue(TEXT("a Point decodes without error"), FGpkg::DecodeGeometry(PointBlob(OriginLon, OriginLat), Parts, bIsLine, Error));
		TestFalse(TEXT("but is not a line"), bIsLine);
		TestEqual(TEXT("and yields no part"), Parts.Num(), 0);
		TestTrue(TEXT("an empty geometry decodes without error"),
			FGpkg::DecodeGeometry(Header(true, 0, true), Parts, bIsLine, Error));
		TestFalse(TEXT("and is not a line"), bIsLine);

		// Malformed: refused, not read past the end.
		TArray<uint8> Truncated = LineBlob(Comet);
		Truncated.SetNum(Truncated.Num() - 5);
		TestFalse(TEXT("a blob cut off inside its points fails"), FGpkg::DecodeGeometry(Truncated, Parts, bIsLine, Error));
		TestFalse(TEXT("a failed decode is not a line"), bIsLine);
		TestFalse(TEXT("a failed decode keeps no part"), Parts.Num() > 0);
		TestFalse(TEXT("a blob with no GeoPackage header fails"),
			FGpkg::DecodeGeometry(TArray<uint8>({ 1, 2, 3 }), Parts, bIsLine, Error));
		TArray<uint8> Lying = Header(true, 0, false);
		Lying.Add(1);
		PutU32(Lying, 2u, true);
		PutU32(Lying, 0x7FFFFFFFu, true); // states far more points than it carries
		TestFalse(TEXT("a point count the blob cannot hold fails"), FGpkg::DecodeGeometry(Lying, Parts, bIsLine, Error));
	}

	// --- ReadBytes: a synthetic GeoPackage through the engine's SQLite module -------------------
	{
		TArray<FRoadRow> Rows;
		Rows.Add({ LineBlob(Comet), TEXT("residential"), TEXT("Comet Rd"), 6.0 });
		Rows.Add({ MultiLineBlob({ { FVector(OriginLon, OriginLat, 2650.0), FVector(OriginLon, OriginLat + 0.001, 2651.0) },
		                           { FVector(OriginLon, OriginLat, 2652.0), FVector(OriginLon - 0.001, OriginLat, 2653.0) } }),
		           TEXT("track"), TEXT(""), 3.5 });
		Rows.Add({ PointBlob(OriginLon, OriginLat), TEXT("none"), TEXT("a point"), 0.0 });
		Rows.Add({ TArray<uint8>(), TEXT("none"), TEXT("no geometry"), 0.0 });
		Rows.Add({ TArray<uint8>({ 'G', 'P', 0, 1, 0, 0, 0, 0, 1 }), TEXT("none"), TEXT("cut off"), 0.0 });

		const FString Path = ScratchPath();
		ON_SCOPE_EXIT { IFileManager::Get().Delete(*Path, false, true, true); };
		if (!TestTrue(TEXT("the synthetic GeoPackage is built"), BuildGpkg(Path, TEXT("RoadSplines"), 4326, Rows)))
		{
			return false;
		}
		TArray<uint8> Bytes;
		if (!TestTrue(TEXT("and can be read back as bytes"), FFileHelper::LoadFileToArray(Bytes, *Path)))
		{
			return false;
		}

		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = -1, Unreadable = -1;
		FString Error;
		TestTrue(TEXT("the layer is read"),
			FGpkg::ReadBytes(Bytes, {}, OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Unreadable, Error));
		TestEqual(TEXT("no error"), Error, FString());
		TestEqual(TEXT("a LineString and a two-part MultiLineString are three splines; the Point and the NULL are not"),
			Splines.Num(), 3);
		TestEqual(TEXT("every line part was seen"), LinesSeen, 3);
		TestEqual(TEXT("the cut-off geometry was counted and passed over"), Unreadable, 1);

		if (Splines.Num() == 3)
		{
			TestEqual(TEXT("width rides along"), Splines[0].WidthMEstimated, 6.0, 1e-9);
			TestEqual(TEXT("class rides along"), Splines[0].RoadClass, FString(TEXT("residential")));
			TestEqual(TEXT("name rides along"), Splines[0].Name, FString(TEXT("Comet Rd")));
			TestEqual(TEXT("origin point lands at world X=0"), Splines[0].PointsUeCm[0].X, 0.0, 5.0);
			TestEqual(TEXT("origin point lands at world Y=0"), Splines[0].PointsUeCm[0].Y, 0.0, 5.0);
			TestEqual(TEXT("Z is orthometric meters -> cm"), Splines[0].PointsUeCm[0].Z, 264000.0, 1e-3);
			TestTrue(TEXT("east of origin -> +Y"), Splines[0].PointsUeCm[1].Y > 8000.0);
			TestTrue(TEXT("north of origin -> +X"), Splines[1].PointsUeCm[1].X > 10000.0);
			TestEqual(TEXT("the second part is its own spline, with the row's class"), Splines[2].RoadClass, FString(TEXT("track")));
			TestEqual(TEXT("and its width"), Splines[2].WidthMEstimated, 3.5, 1e-9);
		}

		// The same roads from GeoJSON are the same splines: one projection, whichever file.
		const FString GeoJson = FString::Printf(
		    TEXT("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\","
		         "\"properties\":{\"class\":\"residential\",\"name\":\"Comet Rd\",\"width_m_estimated\":6.0},"
		         "\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[%.14f,%.14f,2640.0],[%.14f,%.14f,2641.0]]}}]}"),
		    OriginLon, OriginLat, OriginLon + 0.001, OriginLat);
		TArray<FMantlePlaceRoadSpline> FromGeoJson;
		int32 GeoJsonSeen = 0;
		FString GeoJsonError;
		TestTrue(TEXT("the GeoJSON of the same road parses"),
			FLogic::ParseGeoJson(GeoJson, OriginEastingM, OriginNorthingM, Epsg, FromGeoJson, GeoJsonSeen, GeoJsonError));
		if (Splines.Num() == 3 && FromGeoJson.Num() == 1 && FromGeoJson[0].PointsUeCm.Num() == 2)
		{
			for (int32 Index = 0; Index < 2; ++Index)
			{
				TestTrue(TEXT("GeoPackage and GeoJSON place the same point"),
					Splines[0].PointsUeCm[Index].Equals(FromGeoJson[0].PointsUeCm[Index], 1e-6));
			}
		}

		// `tables` narrows the read to the named table; one the file does not hold is refused by name.
		TArray<FMantlePlaceRoadSpline> Named;
		TestTrue(TEXT("a named table that exists is read"),
			FGpkg::ReadBytes(Bytes, { TEXT("RoadSplines") }, OriginEastingM, OriginNorthingM, Epsg, Named, LinesSeen, Unreadable, Error));
		TestEqual(TEXT("with the same roads"), Named.Num(), 3);
		TestFalse(TEXT("a named table the file lacks is refused"),
			FGpkg::ReadBytes(Bytes, { TEXT("RoadSplines_line") }, OriginEastingM, OriginNorthingM, Epsg, Named, LinesSeen, Unreadable, Error));
		TestTrue(TEXT("by name"), Error.Contains(TEXT("RoadSplines_line")));
		TestEqual(TEXT("and nothing is kept"), Named.Num(), 0);

		// A non-UTM origin refuses the layer before the file is opened, as ParseGeoJson does.
		TestFalse(TEXT("a non-UTM origin refuses the layer"),
			FGpkg::ReadBytes(Bytes, {}, OriginEastingM, OriginNorthingM, 3857, Named, LinesSeen, Unreadable, Error));
		TestTrue(TEXT("naming the EPSG"), Error.Contains(TEXT("3857")));
	}

	// --- Refusals: another frame, and not a GeoPackage at all ------------------------------------
	{
		const FString Path = ScratchPath();
		ON_SCOPE_EXIT { IFileManager::Get().Delete(*Path, false, true, true); };
		TestTrue(TEXT("a GeoPackage in Web Mercator is built"),
			BuildGpkg(Path, TEXT("RoadSplines"), 3857, { FRoadRow{ LineBlob(Comet, true, true, true), TEXT("a"), TEXT("b"), 1.0 } }));
		TArray<uint8> Bytes;
		FFileHelper::LoadFileToArray(Bytes, *Path);

		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0, Unreadable = 0;
		FString Error;
		TestFalse(TEXT("a table in another frame is refused, never reprojected"),
			FGpkg::ReadBytes(Bytes, {}, OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Unreadable, Error));
		TestTrue(TEXT("the refusal names the frame"), Error.Contains(TEXT("3857")));
		TestEqual(TEXT("no road is kept"), Splines.Num(), 0);

		const TArray<uint8> NotSqlite = { 'n', 'o', 't', ' ', 'a', ' ', 'd', 'a', 't', 'a', 'b', 'a', 's', 'e' };
		TestFalse(TEXT("bytes that are not a SQLite file are refused"),
			FGpkg::ReadBytes(NotSqlite, {}, OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Unreadable, Error));
		TestFalse(TEXT("with a reason"), Error.IsEmpty());
	}

	// --- A row that lists no `tables` holds one table: read whatever it is named, never picked out of several
	{
		const FString Path = ScratchPath();
		ON_SCOPE_EXIT { IFileManager::Get().Delete(*Path, false, true, true); };
		TestTrue(TEXT("a GeoPackage whose one table is not named for the layer is built"),
			BuildGpkg(Path, TEXT("Streets"), 4326, { FRoadRow{ LineBlob(Comet), TEXT("residential"), TEXT("Comet Rd"), 6.0 } }));

		TArray<FMantlePlaceRoadSpline> Splines;
		int32 LinesSeen = 0, Unreadable = 0;
		FString Error;
		TestTrue(TEXT("an untabled row reads its one table whatever the producer named it"),
			FGpkg::ReadFile(Path, {}, OriginEastingM, OriginNorthingM, Epsg, Splines, LinesSeen, Unreadable, Error));
		TestEqual(TEXT("with no error"), Error, FString());
		TestEqual(TEXT("and its road"), Splines.Num(), 1);

		TestTrue(TEXT("a second feature table is added"), AddFeatureTable(Path, TEXT("Water"), 4326));
		TArray<FMantlePlaceRoadSpline> None;
		TestFalse(TEXT("an untabled row whose file holds two tables is refused, never read as one of them"),
			FGpkg::ReadFile(Path, {}, OriginEastingM, OriginNorthingM, Epsg, None, LinesSeen, Unreadable, Error));
		TestTrue(TEXT("the refusal names the tables"), Error.Contains(TEXT("Streets")) && Error.Contains(TEXT("Water")));
		TestEqual(TEXT("and no road is kept"), None.Num(), 0);

		TArray<FMantlePlaceRoadSpline> Listed;
		TestTrue(TEXT("a row that lists its table reads that table of the same file"),
			FGpkg::ReadFile(Path, { TEXT("Streets") }, OriginEastingM, OriginNorthingM, Epsg, Listed, LinesSeen, Unreadable, Error));
		TestEqual(TEXT("with the same road"), Listed.Num(), 1);
	}

	// --- The manifest pointer: the gpkg row is taken, the geojson row stays the fallback ----------
	{
		const FString Template = TEXT(R"JSON({
  "version": "1.0.0",
  "job_id": "g",
  "vector": { "layers": [ { "name": "road_splines", "formats": [ %FORMATS% ] } ] },
  "hosts": { "unreal": { "mesh_alternative": { "path": "Mesh/Terrain.glb" } } }
})JSON");
		const FString GeoJsonRow = TEXT("{ \"format\": \"geojson\", \"path\": \"Vector/RoadSplines.geojson\", \"sha256\": \"aa\" }");
		const FString GpkgRow = TEXT("{ \"format\": \"gpkg\", \"path\": \"Vector/RoadSplines.gpkg\", \"sha256\": \"bb\", \"tables\": [\"RoadSplines_line\"] }");
		const FString PolygonRow = TEXT("{ \"format\": \"gpkg\", \"geometry_family\": \"polygon\", \"path\": \"Vector/RoadSplines_polygon.gpkg\", \"sha256\": \"cc\" }");

		auto ParseWith = [&](const FString& Formats)
		{
			FString Error;
			return MantlePlaceImportManifest::Parse(Template.Replace(TEXT("%FORMATS%"), *Formats), Error);
		};

		const FMantlePlaceVaultManifest Both = ParseWith(GeoJsonRow + TEXT(",") + GpkgRow);
		TestTrue(TEXT("both rows: the manifest parses"), Both.bValid);
		TestTrue(TEXT("both rows: the gpkg row is read"), Both.RoadSplines.bGpkgPresent);
		TestEqual(TEXT("its path"), Both.RoadSplines.GpkgPath, FString(TEXT("Vector/RoadSplines.gpkg")));
		TestEqual(TEXT("its digest"), Both.RoadSplines.GpkgSha256, FString(TEXT("bb")));
		TestEqual(TEXT("its tables"), Both.RoadSplines.GpkgTables.Num(), 1);
		TestTrue(TEXT("both rows: the geojson row is kept as the fallback"), Both.RoadSplines.bPresent);
		TestEqual(TEXT("its path is still the geojson's"), Both.RoadSplines.Path, FString(TEXT("Vector/RoadSplines.geojson")));

		const FMantlePlaceVaultManifest GpkgOnly = ParseWith(GpkgRow);
		TestTrue(TEXT("gpkg only: the layer is present"), GpkgOnly.RoadSplines.bGpkgPresent);
		TestFalse(TEXT("gpkg only: there is no geojson fallback"), GpkgOnly.RoadSplines.bPresent);

		const FMantlePlaceVaultManifest GeoJsonOnly = ParseWith(GeoJsonRow);
		TestFalse(TEXT("geojson only: no gpkg is declared"), GeoJsonOnly.RoadSplines.bGpkgPresent);
		TestTrue(TEXT("geojson only: the current source is read"), GeoJsonOnly.RoadSplines.bPresent);

		// Another family's file is never taken for the lines'.
		const FMantlePlaceVaultManifest PolygonOnly = ParseWith(PolygonRow);
		TestFalse(TEXT("a polygon-family gpkg row is not the road lines"), PolygonOnly.RoadSplines.bGpkgPresent);

		// Both files are declared payloads, so both are verified before anything is placed.
		const TArray<FMantlePlaceDeclaredArtifact> Declared = FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(Both);
		auto DeclaresPath = [&Declared](const TCHAR* Path)
		{
			return Declared.ContainsByPredicate([Path](const FMantlePlaceDeclaredArtifact& Artifact) { return Artifact.Path == Path; });
		};
		TestTrue(TEXT("the gpkg is verified"), DeclaresPath(TEXT("Vector/RoadSplines.gpkg")));
		TestTrue(TEXT("and so is the geojson"), DeclaresPath(TEXT("Vector/RoadSplines.geojson")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
