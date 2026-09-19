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

        run.Case("the layer table is water and asphalt, and nothing else takes a layer's word", () =>
        {
            // A layer whose subdivisions all wear one material is a rendering decision of its own, so
            // a third entry arrives with a case here; this count is what makes one added without it fail.
            run.Equal(RendererKeywords.ByLayer.Count, 2, "two layers publish one material between them");
            run.Equal(RendererKeywords.ForLayer(GroundLayer.Water), "water", "Enscape reads water out of a material name");
            run.Equal(
                RendererKeywords.ForLayer(GroundLayer.RoadSurface),
                "asphalt",
                "what the surface is called; no renderer documents it as a keyword");
            run.Equal(RendererKeywords.ForLayer(GroundLayer.LandUse), null, "land use reads each feature's own subtype");
            run.Equal(RendererKeywords.ForLayer(GroundLayer.LandCover), null, "and so does land cover");
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

        run.Case("a water body and a road surface read as what they are", () =>
        {
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.Water, "Lake Wendouree", "water"),
                "Mantle Place Site Imagery order-7f3a water body Lake Wendouree water",
                "a plain readable name that ends in the renderer's word");
            run.Equal(
                GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.RoadSurface, "2", "asphalt"),
                "Mantle Place Site Imagery order-7f3a road surface 2 asphalt",
                "the road surfaces are merged per class, so most of them are unnamed");
        });

        run.Case("no two layers share a per-subdivision material", () =>
        {
            // Every layer stamps an unnamed feature by position, so token "1" exists in all four.
            string[] names =
            [
                .. Enum.GetValues<GroundLayer>()
                    .Select(layer => GroundMaterialNames.PerSubDivision(Imagery, layer, "1", null)),
            ];

            run.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Length, "feature 1 of each layer is its own material");
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
