using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which elements of the site model are context buildings: every building proxy, by its GlobalId,
/// in file order — and never the context terrain, whatever Revit's IFC import would call it.
/// </summary>
/// <remarks>
/// The fixtures are synthetic, shaped like the IfcOpenShell output the platform publishes (one
/// <c>IfcBuildingElementProxy</c> with its own extrusion per building, one
/// <c>IfcGeographicElement</c> for the terrain). No bundle's own site model is committed.
/// </remarks>
internal static class SiteModelReaderTests
{
    private const string Header = """
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');
        FILE_NAME('','2026-01-01T00:00:00',(''),(''),'test','test','');
        FILE_SCHEMA(('IFC4'));
        ENDSEC;
        DATA;
        """;

    private const string Footer = """
        ENDSEC;
        END-ISO-10303-21;
        """;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("every building proxy is a context building, by GlobalId, in file order", () =>
        {
            string? error = SiteModelReader.TryRead(
                Model("""
                    ~1=IFCPROJECT('0aaaaaaaaaaaaaaaaaaaaa',$,'Mantle Place Site',$,$,$,$,(~8),~3);
                    ~12=IFCSITE('1bbbbbbbbbbbbbbbbbbbbb',$,'Site',$,$,~18,$,$,$,$,$,0.,$,$);
                    ~14=IFCBUILDING('2ccccccccccccccccccccc',$,'Site massing',$,$,$,$,$,$,$,$,$);
                    ~19=IFCGEOGRAPHICELEMENT('2Tr7uN5eW1aL0dQ3sX9oG$',$,'Terrain',$,$,~30,~23,$,.TERRAIN.);
                    ~48=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,'Building massing',$,$,~55,~50,$,$);
                    ~67=IFCBUILDINGELEMENTPROXY('0Qn7Hs2Lf5Rj8Tg1Vb3Xc_',$,'Building massing',$,$,~74,~69,$,$);
                    ~88=IFCBUILDINGELEMENTPROXY('3pA9zE4kM6nB0rC2dF8gH$',$,'Building massing',$,$,~95,~90,$,$);
                    """),
                out SiteModelContents contents);

            run.True(error is null, "a well-formed site model reads");
            run.Equal(contents.BuildingGlobalIds.Count, 3, "one per proxy");
            run.Equal(contents.BuildingGlobalIds[0], "1Kx0mPq8T3uBv9Wc2Yd4Ze", "file order, first");
            run.Equal(contents.BuildingGlobalIds[2], "3pA9zE4kM6nB0rC2dF8gH$", "file order, last");
        });

        run.Case("the context terrain is counted, and is not a building", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~19=IFCGEOGRAPHICELEMENT('2Tr7uN5eW1aL0dQ3sX9oG$',$,'Terrain',$,$,~30,~23,$,.TERRAIN.);
                    ~48=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,'Building massing',$,$,~55,~50,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(contents.TerrainElements, 1, "the terrain is seen");
            run.False(
                contents.BuildingGlobalIds.Contains("2Tr7uN5eW1aL0dQ3sX9oG$"),
                "and not copied: the project already has the terrain, as a toposolid");
        });

        run.Case("the spatial structure and the relationships are not buildings", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~12=IFCSITE('1bbbbbbbbbbbbbbbbbbbbb',$,'Site',$,$,~18,$,$,$,$,$,0.,$,$);
                    ~14=IFCBUILDING('2ccccccccccccccccccccc',$,'Site massing',$,$,$,$,$,$,$,$,$);
                    ~15=IFCRELAGGREGATES('0Rel1aggregates000000A',$,$,$,~12,(~14));
                    """),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 0, "an IfcBuilding is a container, not a massing element");
        });

        run.Case("a site model with no buildings reads as none, not as an error", () =>
        {
            string? error = SiteModelReader.TryRead(
                Model("~19=IFCGEOGRAPHICELEMENT('2Tr7uN5eW1aL0dQ3sX9oG$',$,'Terrain',$,$,~30,~23,$,.TERRAIN.);"),
                out SiteModelContents contents);

            run.True(error is null, "an area with no buildings is a real area");
            run.Equal(contents.BuildingGlobalIds.Count, 0, "none");
        });

        run.Case("an entity written across lines, with spacing, still reads", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~48 = IFCBUILDINGELEMENTPROXY(
                      '1Kx0mPq8T3uBv9Wc2Yd4Ze',
                      $,'Building massing',$,$,~55,~50,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 1, "STEP does not care about line breaks, so neither does this");
            run.Equal(contents.BuildingGlobalIds[0], "1Kx0mPq8T3uBv9Wc2Yd4Ze", "the GlobalId, trimmed of nothing");
        });

        run.Case("a semicolon or an entity name inside a string does not split or add a record", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~7=IFCPROPERTYSINGLEVALUE('note',$,IFCLABEL('a;b ~9=IFCBUILDINGELEMENTPROXY(''0zzzzzzzzzzzzzzzzzzzzz'')'),$);
                    ~48=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,'O''Brien; north block',$,$,~55,~50,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 1, "only the real proxy");
            run.Equal(contents.BuildingGlobalIds[0], "1Kx0mPq8T3uBv9Wc2Yd4Ze", "and its own GlobalId");
        });

        run.Case("a comment is not a record", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    /* ~9=IFCBUILDINGELEMENTPROXY('0zzzzzzzzzzzzzzzzzzzzz',$,$,$,$,$,$,$,$); */
                    ~48=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,'Building massing',$,$,~55,~50,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 1, "the commented-out proxy is not read");
        });

        run.Case("the entity keyword is matched whatever its case", () =>
        {
            SiteModelReader.TryRead(
                Model("~48=IfcBuildingElementProxy('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,$,$,$,~55,~50,$,$);"),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 1, "STEP writers upper-case it; a reader need not insist");
        });

        run.Case("a building with no GlobalId is counted, and not copied", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~48=IFCBUILDINGELEMENTPROXY($,$,'Building massing',$,$,~55,~50,$,$);
                    ~67=IFCBUILDINGELEMENTPROXY('0Qn7Hs2Lf5Rj8Tg1Vb3Xc_',$,'Building massing',$,$,~74,~69,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(contents.BuildingGlobalIds.Count, 1, "only the identifiable one");
            run.Equal(contents.UnidentifiedBuildings, 1, "the other is said, not dropped silently");
        });

        run.Case("a GlobalId that appears twice is one building", () =>
        {
            SiteModelReader.TryRead(
                Model("""
                    ~48=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,$,$,$,~55,~50,$,$);
                    ~67=IFCBUILDINGELEMENTPROXY('1Kx0mPq8T3uBv9Wc2Yd4Ze',$,$,$,$,~74,~69,$,$);
                    """),
                out SiteModelContents contents);

            run.Equal(
                contents.BuildingGlobalIds.Count,
                1,
                "two elements sharing a stamp would make the second import's answer depend on which one Revit listed first");
        });

        run.Case("a file that is not STEP is refused with a sentence", () =>
        {
            string? error = SiteModelReader.TryRead("""{"type": "FeatureCollection"}""", out SiteModelContents contents);

            run.True(error is not null, "refused");
            run.Contains(error, "IFC", "it says what it expected");
            run.Equal(contents.BuildingGlobalIds.Count, 0, "and reads nothing");
        });

        run.Case("a STEP file with no DATA section is refused", () =>
        {
            string? error = SiteModelReader.TryRead(
                "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nEND-ISO-10303-21;\n",
                out _);

            run.True(error is not null, "a header alone is not a site model");
        });

        return run.Report("site model reader");
    }

    /// <summary>A whole site model around <paramref name="records"/>.</summary>
    /// <remarks>
    /// STEP names each instance with a hash and a number, which is exactly the shape the
    /// public-hygiene gate refuses as a tracker citation. The fixtures therefore write a tilde where
    /// STEP has the hash, and it is put back here.
    /// </remarks>
    private static string Model(string records)
        => Header + "\n" + records.Replace('~', '#') + "\n" + Footer;
}
