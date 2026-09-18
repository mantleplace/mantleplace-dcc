using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The table from a published <c>subtype</c> to the words a renderer reads out of a material name,
/// and the names those words ride on. One case per entry, one for the unknown subtype, and one for
/// every rule about where the keyword sits.
/// </summary>
internal static class RendererKeywordsTests
{
    private const string Imagery = "Mantle Place Site Imagery order-7f3a";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("grass grows medium grass", () =>
            run.Equal(RendererKeywords.For("grass"), "grass", "land_cover grass"));

        run.Case("forest grows wild grass under the canopy", () =>
            run.Equal(RendererKeywords.For("forest"), "wild grass", "the forest floor, not a lawn"));

        run.Case("shrub grows tall grass", () =>
            run.Equal(RendererKeywords.For("shrub"), "tall grass", "scrub reads as tall growth"));

        run.Case("a park grows grass", () =>
            run.Equal(RendererKeywords.For("park"), "grass", "land_use park"));

        run.Case("a golf course grows short grass", () =>
            run.Equal(RendererKeywords.For("golf"), "short grass", "land_use golf"));

        run.Case("the table is exactly the entries above", () =>
        {
            // A new row is a rendering decision, so it arrives with a case of its own above; this
            // count is what makes an entry added without one fail.
            run.Equal(RendererKeywords.Table.Count, 5, "five entries");
            foreach ((string subtype, string keyword) in RendererKeywords.Table)
            {
                run.Equal(RendererKeywords.For(subtype), keyword, $"{subtype} reads back through For");
            }
        });

        run.Case("an unknown subtype adds no keyword", () =>
        {
            run.Equal(RendererKeywords.For("urban"), null, "a published subtype the table does not name");
            run.Equal(RendererKeywords.For("barren"), null, "bare ground grows nothing");
            run.Equal(RendererKeywords.For("wetland"), null, "not open water, so no water keyword either");
            run.Equal(RendererKeywords.For("residential"), null, "land_use residential");
            run.Equal(RendererKeywords.For("some_future_subtype"), null, "a value the schema adds later");
        });

        run.Case("an absent or blank subtype adds no keyword", () =>
        {
            run.Equal(RendererKeywords.For(null), null, "no subtype property");
            run.Equal(RendererKeywords.For(string.Empty), null, "empty");
            run.Equal(RendererKeywords.For("   "), null, "blank");
        });

        run.Case("the match is exact: a subtype is a published value, not a phrase to search", () =>
        {
            run.Equal(RendererKeywords.For("Grass"), null, "Overture publishes lower case; any other spelling is unknown");
            run.Equal(RendererKeywords.For("grassland"), null, "not a substring match");
        });

        run.Case("every keyword keeps its words in the order the renderer reads them", () =>
        {
            // Enscape: "tall grass" grows tall grass and "grass tall" does not. The adjective leads.
            foreach ((_, string keyword) in RendererKeywords.Table)
            {
                run.True(keyword.EndsWith("grass", StringComparison.Ordinal), $"\"{keyword}\" ends in the noun");
            }
        });

        run.Case("a feature's keyword is its subtype's, and a hole claims none", () =>
        {
            SiteVertex[] ring = [new(0, 0, null), new(1, 0, null), new(0, 1, null)];
            run.Equal(
                RendererKeywords.ForRing(new SiteFeature { Vertices = ring, IsClosed = true, Subtype = "forest" }),
                "wild grass",
                "an outer forest ring");
            run.Equal(
                RendererKeywords.ForRing(new SiteFeature { Vertices = ring, IsClosed = true, Subtype = "forest", IsHole = true }),
                null,
                "the clearing inside it is not forest, so it is not named as forest");
            run.Equal(
                RendererKeywords.ForRing(new SiteFeature { Vertices = ring, IsClosed = true, Classification = "grass" }),
                null,
                "the class is not the subtype: land_use class grass under subtype managed stays unnamed");
        });

        run.Case("ByStamp pairs each keyworded ring's stamp with its phrase, and leaves the rest out", () =>
        {
            SiteVertex[] ring = [new(0, 0, null), new(1, 0, null), new(0, 1, null)];
            SiteFeature[] rings =
            [
                new() { Vertices = ring, IsClosed = true, Subtype = "forest" },
                new() { Vertices = ring, IsClosed = true, Subtype = "forest", IsHole = true },
                new() { Vertices = ring, IsClosed = true, Subtype = "urban" },
                new() { Vertices = ring, IsClosed = true, Subtype = "grass" },
            ];

            IReadOnlyDictionary<string, string> byStamp = RendererKeywords.ByStamp(rings, ["c/1", "c/2", "c/3", "c/4"]);
            run.Equal(byStamp.Count, 2, "the forest's outer ring and the grass");
            run.Equal(byStamp.GetValueOrDefault("c/1"), "wild grass", "forest");
            run.Equal(byStamp.GetValueOrDefault("c/2"), null, "the hole");
            run.Equal(byStamp.GetValueOrDefault("c/3"), null, "urban");
            run.Equal(byStamp.GetValueOrDefault("c/4"), "grass", "grass");
        });

        run.Case("ByStamp refuses stamps that do not line up with the rings", () =>
        {
            SiteVertex[] ring = [new(0, 0, null), new(1, 0, null), new(0, 1, null)];
            bool threw = false;
            try
            {
                RendererKeywords.ByStamp([new SiteFeature { Vertices = ring, Subtype = "forest" }], []);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            run.True(threw, "a keyword on the wrong subdivision is worse than none");
        });

        run.Case("the shared material under flat shading is the imagery name plus the keyword", () =>
        {
            run.Equal(
                GroundMaterialNames.Shared(Imagery, "grass"),
                "Mantle Place Site Imagery order-7f3a grass",
                "the issue's own example");
            run.Equal(
                GroundMaterialNames.Shared(Imagery, "wild grass"),
                "Mantle Place Site Imagery order-7f3a wild grass",
                "a two-word keyword keeps its order");
            run.Equal(GroundMaterialNames.Shared(Imagery, null), Imagery, "no keyword is the ground's own material");
        });

        run.Case("a per-subdivision material keeps its old name when there is no keyword", () =>
        {
            // So a re-import of a subdivision whose subtype has no keyword finds the material an
            // earlier build made, rather than growing a second one.
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "Zone A", null),
                "Mantle Place Site Imagery order-7f3a boundary Zone A",
                "the name this plugin has always written");
        });

        run.Case("a per-subdivision material ends with the keyword", () =>
        {
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "Zone A", "grass"),
                "Mantle Place Site Imagery order-7f3a boundary Zone A grass",
                "land use");
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandCover, "3", "wild grass"),
                "Mantle Place Site Imagery order-7f3a land cover 3 wild grass",
                "land cover");
        });

        run.Case("the two layers never share a per-subdivision material", () =>
        {
            // Unnamed features stamp by position in both layers, so token "1" exists twice.
            run.False(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "1", null)
                    == GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandCover, "1", null),
                "land use 1 and land cover 1 carry different offsets and must be different materials");
        });

        run.Case("characters Revit refuses in a name are replaced, and nothing else is", () =>
        {
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, @"A:B{c}[d]|e;f<g>h?i`j~k\l", null),
                "Mantle Place Site Imagery order-7f3a boundary A-B-c--d--e-f-g-h-i-j-k-l",
                "each refused character becomes a hyphen");
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "Café Park 2", "grass"),
                "Mantle Place Site Imagery order-7f3a boundary Café Park 2 grass",
                "accents and spaces are kept");
        });

        return run.Report("renderer keywords");
    }
}
