using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.CarrierSpike;

/// <summary>Which reader a read went through.</summary>
public enum Carrier
{
    GeoJson,
    GeoPackage,
    Dxf,
}

/// <summary>One layer as one carrier gave it, and what that took.</summary>
public sealed record CarrierRead(string Layer, Carrier Carrier, IReadOnlyList<SiteFeature> Features, double Milliseconds, string? Refusal);

/// <summary>
/// Reads one bundle's vector layers through each carrier and says where they differ from today's
/// GeoJSON: features, vertex positions, and each attribute the importer reads. Shared by the
/// headless comparison and the in-Revit probe, so both report the same table.
/// </summary>
public sealed class CarrierComparison
{
    /// <summary>The layer the GeoPackage table and the DXF layer of footprints are named.</summary>
    public const string Footprints = "building_footprints";

    private static readonly SiteGeometryKinds Everything = SiteGeometryKinds.Lines | SiteGeometryKinds.Areas;

    private readonly string _bundle;
    private readonly string _carriers;
    private readonly List<(string Name, string GeoJsonPath, LayerFrame GeoJsonFrame)> _layers = [];
    private string? _dxfText;

    public CarrierComparison(string extractedBundle, string carrierFolder)
    {
        _bundle = extractedBundle;
        _carriers = carrierFolder;
        BundleManifest manifest = BundleManifestReader.Parse(File.ReadAllText(Path.Combine(extractedBundle, "Metadata", "manifest.json")));
        Frame = SiteFrame.For(manifest) ?? throw new InvalidOperationException("The bundle publishes no usable origin.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractedBundle, "Metadata", "manifest.json")));
        JsonElement layers = document.RootElement.GetProperty("hosts").GetProperty("revit").GetProperty("vectors").GetProperty("layers");
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            LinearUnit unit = Unit(layer.GetProperty("units").GetString());
            _layers.Add((layer.GetProperty("name").GetString()!, layer.GetProperty("path").GetString()!, new LayerFrame(LayerCoordinates.AbsoluteProjected, unit)));
        }

        FileUnit = _layers.Count > 0 ? _layers[0].GeoJsonFrame.Unit : LinearUnit.Metre;

        // No Revit GeoJSON carries footprints today (the context buildings come from the site IFC,
        // ADR 0012). The shared lon/lat set is what there is, through the one projection HPS-45
        // permits, and on a State Plane order that projection cannot reach the frame at all; so the
        // baseline is the GeoJSON make_carriers.py wrote in the order's CRS, the source of both carriers.
        _layers.Add((Footprints, Path.Combine(carrierFolder, "footprints.geojson"), new LayerFrame(LayerCoordinates.AbsoluteProjected, FileUnit)));
    }

    /// <summary>How many of the shared lon/lat footprints this order's frame places, of how many there are.</summary>
    public (int Placed, int Features) SharedFootprints()
    {
        string text = File.ReadAllText(Path.Combine(_bundle, "Vector", "Building.geojson"));
        SiteVectorReader.TryParse(text, Frame, LayerFrame.Geographic, Everything, Footprints, out IReadOnlyList<SiteFeature> placed);
        using JsonDocument document = JsonDocument.Parse(text);
        return (placed.Count, document.RootElement.GetProperty("features").GetArrayLength());
    }

    public SiteFrame Frame { get; }

    /// <summary>The unit the carriers are written in: the Revit block's files' own.</summary>
    public LinearUnit FileUnit { get; }

    public IEnumerable<string> Layers => _layers.Select(layer => layer.Name);

    public string GeoPackagePath => Path.Combine(_carriers, "SiteContext.gpkg");

    public string DxfPath => Path.Combine(_carriers, "SiteContext.dxf");

    /// <summary>Reads one layer through one carrier, timed. The DXF's text is read once and its time charged to the first layer.</summary>
    public CarrierRead Read(Carrier carrier, string name)
    {
        (string _, string geoJsonPath, LayerFrame geoJsonFrame) = _layers.First(layer => layer.Name == name);
        LayerFrame carrierFrame = new(LayerCoordinates.AbsoluteProjected, FileUnit);
        Stopwatch clock = Stopwatch.StartNew();
        IReadOnlyList<SiteFeature> features;
        string? refusal = carrier switch
        {
            Carrier.GeoJson => SiteVectorReader.TryParse(File.ReadAllText(Path.Combine(_bundle, geoJsonPath)), Frame, geoJsonFrame, Everything, name, out features, "lowest_level"),
            Carrier.GeoPackage => GeoPackageLayerReader.TryRead(GeoPackagePath, name, Frame, carrierFrame, Everything, name, "lowest_level", out features),
            _ => DxfLayerReader.TryParse(_dxfText ??= File.ReadAllText(DxfPath), name, Frame, carrierFrame, Everything, name, "lowest_level", out features),
        };
        return new CarrierRead(name, carrier, features, clock.Elapsed.TotalMilliseconds, refusal);
    }

    /// <summary>The comparison of one carrier's read of a layer against the GeoJSON baseline, one line of a Markdown table.</summary>
    public static string Compare(CarrierRead baseline, CarrierRead candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Refusal is not null)
        {
            return $"| {candidate.Layer} | {candidate.Carrier} | refused: {candidate.Refusal} | | | |";
        }

        int vertices = 0;
        double worstPlanM = 0.0;
        double worstZM = 0.0;
        int zLost = 0;
        Dictionary<string, int> differs = new(StringComparer.Ordinal);
        Dictionary<string, int> textOnly = new(StringComparer.Ordinal);
        int pairs = Math.Min(baseline.Features.Count, candidate.Features.Count);
        for (int i = 0; i < pairs; i++)
        {
            SiteFeature e = baseline.Features[i];
            SiteFeature a = candidate.Features[i];
            int n = Math.Min(e.Vertices.Count, a.Vertices.Count);
            for (int v = 0; v < n; v++)
            {
                vertices++;
                worstPlanM = Math.Max(worstPlanM, Math.Max(Math.Abs(e.Vertices[v].EastM - a.Vertices[v].EastM), Math.Abs(e.Vertices[v].NorthM - a.Vertices[v].NorthM)));
                if (e.Vertices[v].ElevationM is { } ez)
                {
                    if (a.Vertices[v].ElevationM is { } az)
                    {
                        worstZM = Math.Max(worstZM, Math.Abs(ez - az));
                    }
                    else
                    {
                        zLost++;
                    }
                }
            }

            if (e.Vertices.Count != a.Vertices.Count || e.IsClosed != a.IsClosed || e.IsHole != a.IsHole || e.PolygonOrdinal != a.PolygonOrdinal)
            {
                Tally(differs, "geometry");
            }

            Attribute("name", e.Name, a.Name);
            Attribute("class", e.Classification, a.Classification);
            Attribute("width_m_estimated", Text(e.WidthM), Text(a.WidthM));
            Attribute("subtype", e.Subtype, a.Subtype);
            Attribute("fld_zone", e.FloodZone, a.FloodZone);
            Attribute("zone_subty", e.FloodZoneSubtype, a.FloodZoneSubtype);
            Attribute("threshold_deg", e.Threshold, a.Threshold);
            Attribute("lowest_level", e.LowestLevel ?? string.Empty, a.LowestLevel ?? string.Empty);
        }

        string gaps = differs.Count == 0 && textOnly.Count == 0
            ? "none"
            : string.Join("; ", differs.Select(d => $"{d.Key} differs on {d.Value}").Concat(textOnly.Select(t => $"{t.Key} same number, other text on {t.Value}")));
        string z = zLost > 0 ? $"Z lost on {zLost}" : worstZM.ToString("0.###E+0", CultureInfo.InvariantCulture);
        return $"| {candidate.Layer} | {candidate.Carrier} | {candidate.Features.Count} of {baseline.Features.Count} | "
            + $"{worstPlanM.ToString("0.###E+0", CultureInfo.InvariantCulture)} m plan, {z} m Z, over {vertices} vertices | {gaps} | "
            + $"{candidate.Milliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms |";

        void Attribute(string key, string expected, string actual)
        {
            if (expected == actual)
            {
                return;
            }

            bool sameNumber = double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && x == y;
            Tally(sameNumber ? textOnly : differs, key);
        }
    }

    /// <summary>The whole table: every layer, both candidates against the baseline.</summary>
    public string Table()
    {
        StringBuilder table = new();
        table.AppendLine("| layer | carrier | features | worst vertex delta against GeoJSON | attribute gaps | read time |");
        table.AppendLine("|---|---|---|---|---|---|");
        foreach (string name in Layers)
        {
            CarrierRead baseline = Read(Carrier.GeoJson, name);
            table.AppendLine($"| {name} | GeoJson | {baseline.Features.Count} | baseline | baseline | {baseline.Milliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms |");
            table.AppendLine(Compare(baseline, Read(Carrier.GeoPackage, name)));
            table.AppendLine(Compare(baseline, Read(Carrier.Dxf, name)));
        }

        (int placed, int features) = SharedFootprints();
        table.AppendLine();
        table.AppendLine($"The shared lon/lat Vector/Building.geojson through the frame's one projection: {placed} of {features} footprints placed.");
        return table.ToString();
    }

    private static string Text(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Tally(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static LinearUnit Unit(string? token) => token switch
    {
        "ftUS" or "us-ft" => LinearUnit.UsSurveyFoot,
        "ft" => LinearUnit.InternationalFoot,
        _ => LinearUnit.Metre,
    };
}
