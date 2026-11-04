using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The public-record set read file by file: a record DXF the reader refuses (a <c>POINT</c> entity is
/// the one the platform's contamination and historic-places files hold) is refused on its own, by name,
/// and the other record layers are still read and placed.
/// </summary>
internal static class AutoCadRecordSetTests
{
    internal static int Run()
    {
        TestRun run = new();

        static PlannedFile File(string name) =>
            new(name, $"Public-Records/{name}.dxf", new string('a', 64), "EPSG:2231", "us-survey-foot", "us-survey-foot", AutoCadLayers.Record(name));

        static string Entities(params string[] entities) =>
            "0\nSECTION\n2\nENTITIES\n" + string.Concat(entities) + "0\nENDSEC\n0\nEOF\n";

        const string Polyline = "0\nLWPOLYLINE\n8\nparcels\n90\n2\n70\n0\n10\n0.0\n20\n0.0\n10\n1.0\n20\n1.0\n";
        const string Point = "0\nPOINT\n8\ncontamination\n10\n5.0\n20\n5.0\n30\n0.0\n";

        Dictionary<string, string> sources = new(StringComparer.Ordinal)
        {
            ["parcels"] = Entities(Polyline),
            ["zoning"] = Entities(Polyline, Polyline),
            ["contamination"] = Entities(Point),
            ["historic-places"] = Entities(Polyline, Point),
        };

        AutoCadRecordSet Read(params string[] names) =>
            AutoCadRecordSet.Read([.. names.Select(File)], f => new StringReader(sources[f.Name]));

        run.Case("a record set every file of which the reader takes is read whole", () =>
        {
            AutoCadRecordSet set = Read("parcels", "zoning");

            run.Equal(set.Layers.Count, 2, "both layers read");
            run.Equal(set.Refused.Count, 0, "none refused");
            run.Equal(set.Layers[1].Polylines.Count, 2, "zoning's two polylines");
        });

        run.Case("a record DXF holding POINT entities is refused on its own, by name, and the other layers are still read", () =>
        {
            AutoCadRecordSet set = Read("parcels", "contamination", "zoning");

            run.Equal(set.Layers.Count, 2, "the two other layers read");
            run.Equal(set.Layers[0].File.Name, "parcels", "parcels kept, in the block's order");
            run.Equal(set.Layers[1].File.Name, "zoning", "zoning kept");
            run.Equal(set.Refused.Count, 1, "one refused");
            run.Equal(set.Refused[0].Name, "contamination", "named");
            run.Contains(set.Refused[0].Why, "The contamination DXF", "the reader's reason names the file");
            run.Contains(set.Refused[0].Why, "POINT", "and the entity");
        });

        run.Case("a file mixing polylines and POINT entities is refused whole, never half placed", () =>
        {
            AutoCadRecordSet set = Read("historic-places", "parcels");

            run.Equal(set.Layers.Count, 1, "only parcels read");
            run.Equal(set.Refused[0].Name, "historic-places", "named");
        });

        run.Case("a set of which every file is refused has no layers and says why for each", () =>
        {
            AutoCadRecordSet set = Read("contamination", "historic-places");

            run.Equal(set.Layers.Count, 0, "nothing to place");
            run.Equal(set.Refused.Count, 2, "both refused");
            run.Contains(set.RefusalSentence(), "contamination", "the first, named");
            run.Contains(set.RefusalSentence(), "historic-places", "the second, named");
        });

        run.Case("the placed sentence counts only the layers placed and names the layers left out", () =>
        {
            AutoCadRecordSet set = Read("parcels", "contamination", "zoning");
            PlannedPart part = new() { Kind = AutoCadPart.Records, Place = true, Present = true, Ready = true, Sentence = string.Empty, Files = [File("parcels"), File("contamination"), File("zoning")] };

            string said = AutoCadImportPlanner.RecordsPlacedSentence(part, 3, set.Refused);

            run.Contains(said, "3 polylines", "the count");
            run.Contains(said, "on 2 layers", "only the layers placed");
            run.Contains(said, "MantlePlace-Records-parcels", "a placed layer");
            run.True(!said.Contains("MantlePlace-Records-contamination", StringComparison.Ordinal), "the refused layer is not listed as placed");
            run.Contains(said, "contamination", "the refused layer is named");
            run.Contains(said, "POINT", "with the reason");
        });

        return run.Report("AutoCAD record set");
    }
}
