// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlacePublicRecordsLogic.h"

namespace
{
/** A drawing with the given ENTITIES body (group-code/value lines) and the sections a real one carries. */
FString Drawing(const FString& EntitiesBody)
{
	return TEXT("0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1015\n0\nENDSEC\n")
	       TEXT("0\nSECTION\n2\nTABLES\n0\nTABLE\n2\nLAYER\n0\nENDTAB\n0\nENDSEC\n")
	       TEXT("0\nSECTION\n2\nENTITIES\n")
	    + EntitiesBody + TEXT("0\nENDSEC\n0\nEOF\n");
}

FMantlePlaceRecordFrame Projected(const TCHAR* Crs, const TCHAR* HorizontalUnit)
{
	FMantlePlaceRecordFrame Frame;
	Frame.bStated = true;
	Frame.Type = TEXT("projected");
	Frame.Crs = Crs;
	Frame.HorizontalUnit = HorizontalUnit;
	Frame.VerticalUnit = TEXT("m");
	return Frame;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlacePublicRecordsFamiliesTest,
    "MantlePlace.Import.PublicRecordsLogic.Families",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlacePublicRecordsFamiliesTest::RunTest(const FString& Parameters)
{
	// The ten families the published layout names, in its order; a family nobody has met comes after.
	for (const TCHAR* Known : { TEXT("parcels"), TEXT("zoning"), TEXT("flood"), TEXT("wetlands"), TEXT("soils"),
	                            TEXT("protected"), TEXT("hydrography"), TEXT("habitat"), TEXT("contamination"),
	                            TEXT("historic") })
	{
		TestTrue(FString::Printf(TEXT("%s is a known family"), Known), FMantlePlacePublicRecordsLogic::IsKnownFamily(Known));
	}
	TestFalse(TEXT("an unseen family is not known"), FMantlePlacePublicRecordsLogic::IsKnownFamily(TEXT("easements")));
	TestFalse(TEXT("a known family is matched exactly"), FMantlePlacePublicRecordsLogic::IsKnownFamily(TEXT("Parcels")));

	TestEqual(TEXT("flood zones read as the user says them"),
		FMantlePlacePublicRecordsLogic::DisplayName(TEXT("flood")), FString(TEXT("Flood zones")));
	TestEqual(TEXT("protected areas"),
		FMantlePlacePublicRecordsLogic::DisplayName(TEXT("protected")), FString(TEXT("Protected areas")));
	TestEqual(TEXT("an unseen family is its own key in words"),
		FMantlePlacePublicRecordsLogic::DisplayName(TEXT("scenic_easements")), FString(TEXT("Scenic Easements")));
	TestEqual(TEXT("a hyphenated family"),
		FMantlePlacePublicRecordsLogic::DisplayName(TEXT("land-use")), FString(TEXT("Land Use")));

	TArray<FMantlePlaceRecordLayer> Layers;
	for (const TCHAR* Family : { TEXT("easements"), TEXT("historic"), TEXT("flood"), TEXT("aquifers"), TEXT("parcels") })
	{
		FMantlePlaceRecordLayer Layer;
		Layer.Family = Family;
		Layers.Add(Layer);
	}
	FMantlePlacePublicRecordsLogic::OrderLayers(Layers);
	TArray<FString> Order;
	for (const FMantlePlaceRecordLayer& Layer : Layers)
	{
		Order.Add(Layer.Family);
	}
	TestEqual(TEXT("known families in the published order, then the rest by key"),
		FString::Join(Order, TEXT(",")), FString(TEXT("parcels,flood,historic,aquifers,easements")));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlacePublicRecordsFrameTest,
    "MantlePlace.Import.PublicRecordsLogic.Frame",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlacePublicRecordsFrameTest::RunTest(const FString& Parameters)
{
	const FString Host = TEXT("EPSG:32613");
	FString Reason;

	TestTrue(TEXT("a stated frame equal to the host's is placed"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Projected(TEXT("EPSG:32613"), TEXT("m")), Host, Reason));
	TestEqual(TEXT("a placed frame has no reason"), Reason, FString());

	// HPS-53: an unstated frame is never assumed to match.
	TestFalse(TEXT("no frame stated is unplaceable"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(FMantlePlaceRecordFrame(), Host, Reason));
	TestTrue(TEXT("and the reason says it was not stated"), Reason.Contains(TEXT("states no frame")));

	TestFalse(TEXT("another grid is unplaceable"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Projected(TEXT("EPSG:6543"), TEXT("m")), Host, Reason));
	TestTrue(TEXT("and the reason names both grids"), Reason.Contains(TEXT("EPSG:6543")) && Reason.Contains(TEXT("EPSG:32613")));

	TestFalse(TEXT("the same grid in feet is unplaceable, never scaled"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Projected(TEXT("EPSG:32613"), TEXT("ftUS")), Host, Reason));
	TestTrue(TEXT("and the reason names the unit"), Reason.Contains(TEXT("ftUS")));

	FMantlePlaceRecordFrame Local = Projected(TEXT("EPSG:32613"), TEXT("m"));
	Local.Type = TEXT("local");
	TestFalse(TEXT("a local frame is not this host's grid"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Local, Host, Reason));

	FMantlePlaceRecordFrame Partial;
	Partial.bStated = true;
	Partial.Type = TEXT("projected");
	Partial.Crs = TEXT("EPSG:32613");
	TestFalse(TEXT("a frame stated in part is unplaceable"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Partial, Host, Reason));
	TestTrue(TEXT("and the reason says in part"), Reason.Contains(TEXT("only in part")));

	TestFalse(TEXT("a host that names no CRS places nothing"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Projected(TEXT("EPSG:32613"), TEXT("m")), FString(), Reason));
	TestFalse(TEXT("the CRS is compared verbatim"),
		FMantlePlacePublicRecordsLogic::CanPlaceFrame(Projected(TEXT("epsg:32613"), TEXT("m")), Host, Reason));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlacePublicRecordsDxfTest,
    "MantlePlace.Import.PublicRecordsLogic.Dxf",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlacePublicRecordsDxfTest::RunTest(const FString& Parameters)
{
	TArray<FMantlePlaceRecordPolyline> Lines;
	int32 Skipped = 0;
	FString Error;

	// --- LWPOLYLINE, closed by flag 70, with the vertex pairs it repeats ---------------------
	{
		const FString Text = Drawing(
		    TEXT("0\nLWPOLYLINE\n8\nPARCELS\n90\n4\n70\n1\n")
		    TEXT("10\n441959.5\n20\n4014372.5\n")
		    TEXT("10\n442059.5\n20\n4014372.5\n")
		    TEXT("10\n442059.5\n20\n4014472.5\n")
		    TEXT("10\n441959.5\n20\n4014472.5\n"));
		TestTrue(TEXT("lwpolyline parses"), FMantlePlacePublicRecordsLogic::ParseDxf(Text, Lines, Skipped, Error));
		TestEqual(TEXT("no error"), Error, FString());
		TestEqual(TEXT("one line"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("four vertices"), Lines[0].Points.Num(), 4);
			TestTrue(TEXT("closed by flag 70"), Lines[0].bClosed);
			TestEqual(TEXT("easting is kept as stated"), Lines[0].Points[1].X, 442059.5, 1e-6);
			TestEqual(TEXT("northing is kept as stated"), Lines[0].Points[2].Y, 4014472.5, 1e-6);
		}
		TestEqual(TEXT("nothing left out"), Skipped, 0);
	}

	// --- POLYLINE with VERTEX records until SEQEND; its own 10/20/30 is a dummy elevation ----
	{
		const FString Text = Drawing(
		    TEXT("0\nPOLYLINE\n8\nZONING\n66\n1\n10\n0.0\n20\n0.0\n30\n0.0\n70\n0\n")
		    TEXT("0\nVERTEX\n8\nZONING\n10\n100.0\n20\n200.0\n30\n0.0\n")
		    TEXT("0\nVERTEX\n8\nZONING\n10\n150.0\n20\n250.0\n30\n0.0\n")
		    TEXT("0\nVERTEX\n8\nZONING\n10\n170.0\n20\n260.0\n30\n0.0\n")
		    TEXT("0\nSEQEND\n8\nZONING\n"));
		TestTrue(TEXT("polyline parses"), FMantlePlacePublicRecordsLogic::ParseDxf(Text, Lines, Skipped, Error));
		TestEqual(TEXT("one line"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("the dummy elevation record is not a vertex"), Lines[0].Points.Num(), 3);
			TestFalse(TEXT("open"), Lines[0].bClosed);
			TestEqual(TEXT("first vertex"), Lines[0].Points[0].X, 100.0, 1e-9);
		}
	}

	// --- A polyface mesh is not a line; LINE is two points; other entities are counted --------
	{
		const FString Text = Drawing(
		    TEXT("0\nPOLYLINE\n70\n64\n")
		    TEXT("0\nVERTEX\n70\n192\n10\n1.0\n20\n1.0\n")
		    TEXT("0\nVERTEX\n70\n192\n10\n2.0\n20\n2.0\n")
		    TEXT("0\nSEQEND\n")
		    TEXT("0\nLINE\n10\n10.0\n20\n20.0\n11\n30.0\n21\n40.0\n")
		    TEXT("0\nTEXT\n10\n5.0\n20\n5.0\n1\nlabel\n")
		    TEXT("0\nPOINT\n10\n6.0\n20\n6.0\n"));
		TestTrue(TEXT("mixed drawing parses"), FMantlePlacePublicRecordsLogic::ParseDxf(Text, Lines, Skipped, Error));
		TestEqual(TEXT("only the LINE is a line"), Lines.Num(), 1);
		if (Lines.Num() == 1)
		{
			TestEqual(TEXT("a LINE has two points"), Lines[0].Points.Num(), 2);
			TestEqual(TEXT("its end easting"), Lines[0].Points[1].X, 30.0, 1e-9);
			TestEqual(TEXT("its end northing"), Lines[0].Points[1].Y, 40.0, 1e-9);
		}
		TestEqual(TEXT("the polyface, the text and the point are counted as left out"), Skipped, 3);
	}

	// --- CRLF line ends, and a drawing with no lines in it is a success with none ------------
	{
		const FString Text = Drawing(TEXT("0\nTEXT\n10\n5.0\n20\n5.0\n1\nlabel\n")).Replace(TEXT("\n"), TEXT("\r\n"));
		TestTrue(TEXT("CRLF drawing parses"), FMantlePlacePublicRecordsLogic::ParseDxf(Text, Lines, Skipped, Error));
		TestEqual(TEXT("no lines"), Lines.Num(), 0);
		TestEqual(TEXT("the text is counted"), Skipped, 1);
	}

	// --- Fail closed on what is not an ASCII drawing -----------------------------------------
	{
		TestFalse(TEXT("binary DXF is refused"),
			FMantlePlacePublicRecordsLogic::ParseDxf(TEXT("AutoCAD Binary DXF\r\n"), Lines, Skipped, Error));
		TestTrue(TEXT("and named as binary"), Error.Contains(TEXT("binary")));

		TestFalse(TEXT("text that is not group codes is refused"),
			FMantlePlacePublicRecordsLogic::ParseDxf(TEXT("hello\nworld\n"), Lines, Skipped, Error));
		TestTrue(TEXT("and the line is named"), Error.Contains(TEXT("line 1")));

		TestFalse(TEXT("a drawing with no ENTITIES section is refused"),
			FMantlePlacePublicRecordsLogic::ParseDxf(TEXT("0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nEOF\n"), Lines, Skipped, Error));
		TestTrue(TEXT("and the missing section is named"), Error.Contains(TEXT("ENTITIES")));
		TestEqual(TEXT("a refusal keeps no lines"), Lines.Num(), 0);
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlacePublicRecordsSiteTest,
    "MantlePlace.Import.PublicRecordsLogic.Site",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlacePublicRecordsSiteTest::RunTest(const FString& Parameters)
{
	const double OriginEastingM = 441959.5;
	const double OriginNorthingM = 4014372.5;
	// A landscape 1000 m (north) by 2000 m (east) in UE cm, centred on the origin.
	const FVector2D Span(100000.0, 200000.0);

	auto LineAt = [](double Easting, double Northing)
	{
		FMantlePlaceRecordPolyline Line;
		Line.Points.Add(FVector2D(Easting, Northing));
		Line.Points.Add(FVector2D(Easting + 1.0, Northing + 1.0));
		return Line;
	};

	TArray<FMantlePlaceRecordPolyline> OnSite = { LineAt(OriginEastingM + 900.0, OriginNorthingM + 400.0) };
	TestTrue(TEXT("a vertex inside the span (900 m east of 1000, 400 m north of 500) is kept"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(OnSite, OriginEastingM, OriginNorthingM, Span));

	// The axes are not interchangeable: 700 m north is off a span that is 500 m each side of the
	// origin in the north direction, however much room there is east.
	TArray<FMantlePlaceRecordPolyline> NorthOfIt = { LineAt(OriginEastingM, OriginNorthingM + 700.0) };
	TestFalse(TEXT("a file wholly beyond the northern half-span is not in this frame"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(NorthOfIt, OriginEastingM, OriginNorthingM, Span));

	// Foot State Plane against a metre origin: a number that looks like a position and is a thousand
	// kilometres away.
	TArray<FMantlePlaceRecordPolyline> StatePlane = { LineAt(2400000.0, 1500000.0) };
	TestFalse(TEXT("another grid's coordinates fall off the site"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(StatePlane, OriginEastingM, OriginNorthingM, Span));

	TArray<FMantlePlaceRecordPolyline> Straddles = { LineAt(OriginEastingM - 5000.0, OriginNorthingM + 5000.0),
		                                              LineAt(OriginEastingM + 10.0, OriginNorthingM + 10.0) };
	TestTrue(TEXT("one vertex on site keeps a layer clipped to an area wider than the landscape"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(Straddles, OriginEastingM, OriginNorthingM, Span));

	TestTrue(TEXT("no published span is no evidence either way"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(StatePlane, OriginEastingM, OriginNorthingM, FVector2D::ZeroVector));
	TestTrue(TEXT("a file with no lines is not refused on this evidence"),
		FMantlePlacePublicRecordsLogic::AnyVertexOnSite(TArray<FMantlePlaceRecordPolyline>(), OriginEastingM, OriginNorthingM, Span));
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlacePublicRecordsWordsTest,
    "MantlePlace.Import.PublicRecordsLogic.Words",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlacePublicRecordsWordsTest::RunTest(const FString& Parameters)
{
	// The standing notice says what the user must hear whatever the manifest carries.
	const FString Notice = FMantlePlacePublicRecordsLogic::StandingNotice();
	TestTrue(TEXT("the notice says not a survey"), Notice.Contains(TEXT("not a survey")));
	TestTrue(TEXT("and not a legal determination"), Notice.Contains(TEXT("not a legal determination")));

	// A layer's credit: its own, else the source its file entry names with the licence.
	{
		FMantlePlaceRecordLayer Layer;
		Layer.Family = TEXT("wetlands");
		FMantlePlaceRecordFile File;
		File.Kind = TEXT("gpkg");
		File.Source = TEXT("U.S. Fish and Wildlife Service, National Wetlands Inventory");
		File.License = TEXT("public-domain");
		Layer.Files.Add(File);
		TArray<FString> Lines = FMantlePlacePublicRecordsLogic::DescribeLayer(Layer);
		TestEqual(TEXT("one line when the manifest carries no disclaimer or notice"), Lines.Num(), 1);
		TestTrue(TEXT("the line names the layer, the source and the licence"),
			Lines.Num() > 0 && Lines[0].StartsWith(TEXT("Wetlands:")) && Lines[0].Contains(TEXT("National Wetlands Inventory"))
				&& Lines[0].Contains(TEXT("public-domain")));

		Layer.Credit = TEXT("Example County Assessor");
		Layer.Disclaimer = TEXT("Shown as the holder publishes it.");
		Layer.Notice = TEXT("Not a survey, not a legal determination.");
		Lines = FMantlePlacePublicRecordsLogic::DescribeLayer(Layer);
		TestEqual(TEXT("credit, disclaimer and notice"), Lines.Num(), 3);
		TestTrue(TEXT("the layer's own credit wins over the file's source"),
			Lines.Num() > 0 && Lines[0].Contains(TEXT("Example County Assessor")) && !Lines[0].Contains(TEXT("Wetlands Inventory")));
		TestTrue(TEXT("the disclaimer is shown as published"), Lines.Num() > 1 && Lines[1].Contains(TEXT("Shown as the holder publishes it.")));
		TestTrue(TEXT("the notice is shown as published"), Lines.Num() > 2 && Lines[2].Contains(TEXT("Not a survey, not a legal determination.")));

		FMantlePlaceRecordLayer Bare;
		Bare.Family = TEXT("easements");
		const TArray<FString> BareLines = FMantlePlacePublicRecordsLogic::DescribeLayer(Bare);
		TestTrue(TEXT("a layer with no credit says so rather than saying nothing"),
			BareLines.Num() == 1 && BareLines[0].Contains(TEXT("no credit line")));
	}

	// A record the bundle does not deliver: who, why, and where the holder publishes it.
	{
		FMantlePlaceRecordPointer Pointer;
		Pointer.RecordHolder = TEXT("Example County");
		Pointer.Jurisdiction = TEXT("Example County");
		Pointer.Link = TEXT("https://www.example.org/county/parcels");
		Pointer.Reason = TEXT("no_open_source");
		const FString Sentence = FMantlePlacePublicRecordsLogic::DescribePointer(TEXT("parcels"), Pointer);
		TestTrue(TEXT("names the layer"), Sentence.StartsWith(TEXT("Parcels not delivered")));
		TestTrue(TEXT("names the holder"), Sentence.Contains(TEXT("Example County")));
		TestTrue(TEXT("gives where the holder publishes it"), Sentence.Contains(TEXT("https://www.example.org/county/parcels")));
		TestTrue(TEXT("gives why"), Sentence.Contains(TEXT("no open source")));

		Pointer.Link.Reset();
		Pointer.Reason = TEXT("terms_restrict_commercial_use");
		Pointer.Basis = TEXT("state statute");
		const FString NoLink = FMantlePlacePublicRecordsLogic::DescribePointer(TEXT("zoning"), Pointer);
		TestTrue(TEXT("a holder with no known page says so"), NoLink.Contains(TEXT("gives no page")));
		TestTrue(TEXT("the terms reason carries its basis"), NoLink.Contains(TEXT("commercial use")) && NoLink.Contains(TEXT("state statute")));

		Pointer.Reason = TEXT("a_reason_from_the_future");
		TestTrue(TEXT("an unknown reason reads as no open source: the record is not here either way"),
			FMantlePlacePublicRecordsLogic::DescribePointer(TEXT("parcels"), Pointer).Contains(TEXT("no open source")));

		Pointer.Reason = TEXT("down_at_build_time");
		TestTrue(TEXT("a source that was down says so"),
			FMantlePlacePublicRecordsLogic::DescribePointer(TEXT("parcels"), Pointer).Contains(TEXT("was down")));
	}

	// A source that covers the extent and delivered nothing.
	{
		FMantlePlaceSkippedSource Source;
		Source.Layer = TEXT("zoning");
		Source.SourceId = TEXT("example-city-zoning");
		Source.RecordHolder = TEXT("Example City");
		Source.Reason = TEXT("source_timeout");
		const FString Sentence = FMantlePlacePublicRecordsLogic::DescribeSkippedSource(Source);
		TestTrue(TEXT("names layer, holder and source"),
			Sentence.Contains(TEXT("Zoning")) && Sentence.Contains(TEXT("Example City")) && Sentence.Contains(TEXT("example-city-zoning")));
		TestTrue(TEXT("a timeout reads as one"), Sentence.Contains(TEXT("ran out of time")));

		Source.Reason = TEXT("something_new");
		TestTrue(TEXT("an unknown reason reads as a failure to answer"),
			FMantlePlacePublicRecordsLogic::DescribeSkippedSource(Source).Contains(TEXT("did not answer")));

		Source.Layer.Reset();
		TestTrue(TEXT("a layer this reader cannot name is shown under its holder"),
			FMantlePlacePublicRecordsLogic::DescribeSkippedSource(Source).StartsWith(TEXT("A record from Example City")));
	}

	// All of them together, pointers first and sources after, nothing when there is nothing.
	{
		FMantlePlacePublicRecordsBlock Block;
		TestEqual(TEXT("nothing missing, nothing said"), FMantlePlacePublicRecordsLogic::DescribeNotDelivered(Block).Num(), 0);

		FMantlePlaceRecordLayer Layer;
		Layer.Family = TEXT("parcels");
		FMantlePlaceRecordPointer Pointer;
		Pointer.RecordHolder = TEXT("Example County");
		Pointer.Reason = TEXT("no_open_source");
		Layer.Pointers.Add(Pointer);
		Block.Layers.Add(Layer);
		FMantlePlaceSkippedSource Source;
		Source.Layer = TEXT("zoning");
		Source.RecordHolder = TEXT("Example City");
		Source.SourceId = TEXT("s");
		Source.Reason = TEXT("source_down");
		Block.SkippedSources.Add(Source);
		const TArray<FString> Lines = FMantlePlacePublicRecordsLogic::DescribeNotDelivered(Block);
		TestEqual(TEXT("one pointer, one source"), Lines.Num(), 2);
		TestTrue(TEXT("the pointer first"), Lines.Num() > 0 && Lines[0].StartsWith(TEXT("Parcels")));
		TestTrue(TEXT("the source after"), Lines.Num() > 1 && Lines[1].StartsWith(TEXT("Zoning")));
	}
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
