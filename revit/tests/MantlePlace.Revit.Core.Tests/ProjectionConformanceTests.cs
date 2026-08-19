using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Drives the shared corpus' <c>projection</c> group (<c>HPS-45</c>).
/// </summary>
/// <remarks>
/// <para>
/// This host claimed no <c>projection</c> group until the vector layers landed, and said so in its
/// <c>verified-against.json</c> evidence, because every placement value it consumed arrived
/// pre-derived (<c>HPS-33</c>). The road-centreline and site-boundary layers change that and only
/// that: their GeoJSON is RFC 7946 lon/lat, so the manifest describes the LAYER while every vertex
/// inside it is still geographic — the exact narrow case <c>HPS-45</c> permits a host to project
/// for, and the only one it permits.
/// </para>
/// <para>
/// Getting a zone or a false northing wrong places geometry kilometres away while every test that
/// does not check numbers still passes, which is why the group is a known-answer table rather than
/// a round-trip.
/// </para>
/// </remarks>
internal static class ProjectionConformanceTests
{
    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.LoadGroup("projection", out List<ConformanceCorpus.CorpusCase> cases) is { } problem)
        {
            run.Fail(problem);
            return run.Report("projection conformance");
        }

        HashSet<string> driven = new(StringComparer.Ordinal);

        foreach (ConformanceCorpus.CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () =>
            {
                using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);

                switch (corpusCase.Id)
                {
                    case "projection.lonLatToUtm":
                        DriveLonLatToUtm(run, vectors.Root);
                        break;
                    default:
                        run.Fail($"no driver for corpus case '{corpusCase.Id}' (HPS-41)");
                        return;
                }

                driven.Add(corpusCase.Id);

                foreach (string unread in vectors.UnreadPaths())
                {
                    run.Fail($"'{unread}' is a vector value nothing in this suite read.");
                }
            });
        }

        foreach (string undriven in ConformanceCorpus.UndrivenCases(cases, driven))
        {
            run.Fail($"corpus case '{undriven}' loaded but nothing drove it (HPS-41)");
        }

        return run.Report("projection conformance");
    }

    private static void DriveLonLatToUtm(TestRun run, VectorNode root)
    {
        // Read rather than ignored: the table states which fixture family its numbers came from, and
        // a suite that silently drove a different AOI's origin would still be green.
        run.Contains(root.Str("source"), "mesh.origin", "the table names the fixture family it came from");

        double tolerance = root.Double("toleranceMetres") ?? 0.0;
        run.True(tolerance > 0.0, "the table states a tolerance");

        foreach (VectorNode pair in root.Items("pairs"))
        {
            double lon = pair.Double("lonDeg") ?? 0.0;
            double lat = pair.Double("latDeg") ?? 0.0;
            int epsg = pair.Int("epsg") ?? 0;

            run.True(
                GeoProjection.TryLonLatToUtm(lon, lat, epsg, out double easting, out double northing),
                $"EPSG:{epsg} projects");
            run.Within(easting, pair.Double("eastingM") ?? 0.0, tolerance, $"EPSG:{epsg} easting");
            run.Within(northing, pair.Double("northingM") ?? 0.0, tolerance, $"EPSG:{epsg} northing");
        }

        foreach (VectorNode south in root.Items("southernHemisphere"))
        {
            int epsg = south.Int("epsg") ?? 0;
            run.True(
                GeoProjection.TryLonLatToUtm(
                    south.Double("lonDeg") ?? 0.0,
                    south.Double("latDeg") ?? 0.0,
                    epsg,
                    out _,
                    out double northing),
                $"EPSG:{epsg} projects");

            // The 10 000 km false northing is the whole of this row. Without it a southern-hemisphere
            // site lands north of the equator, which no non-numeric assertion would notice.
            run.True(
                northing > (south.Double("northingGreaterThan") ?? 0.0),
                $"EPSG:{epsg} applies the southern false northing");
        }

        foreach (VectorNode reject in root.Items("rejects"))
        {
            int epsg = reject.Int("epsg") ?? 0;
            run.False(
                GeoProjection.TryLonLatToUtm(
                    reject.Double("lonDeg") ?? 0.0,
                    reject.Double("latDeg") ?? 0.0,
                    epsg,
                    out _,
                    out _),
                $"EPSG:{epsg} is refused rather than projected at a guess");
        }
    }
}
