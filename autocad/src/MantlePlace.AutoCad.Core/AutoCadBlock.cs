using System.Text.Json;
using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public sealed record AutoCadFileFrame(
    string Type,
    string Crs,
    string BaseCrs,
    double? OriginEasting,
    double? OriginNorthing,
    string OriginLinearUnit,
    string HorizontalUnit,
    string VerticalUnit)
{
    public const string Projected = "projected";

    public const string Local = "local";

    public static AutoCadFileFrame None { get; } = new(string.Empty, string.Empty, string.Empty, null, null, string.Empty, string.Empty, string.Empty);
}

public sealed record AerialInsertion(double X, double Y, double Width, double Height, double RotationDeg);

public sealed record PartReadiness(bool Stated, bool Present, string? Reason, string? Requires)
{
    public static PartReadiness Unstated { get; } = new(false, false, null, null);
}

public sealed record AutoCadPointer(
    string Path,
    string Sha256,
    string Format,
    string HorizontalFrame,
    string HorizontalUnits,
    string VerticalUnits,
    string VerticalReference,
    string VerticalDatum,
    int PixelWidth,
    int PixelHeight,
    AerialInsertion? Insertion);

public sealed record AutoCadRecordLayer(string Name, AutoCadPointer Pointer);

public sealed class AutoCadBlock
{
    public required AutoCadFileFrame FileFrame { get; init; }

    public AutoCadPointer? Contours { get; init; }

    public AutoCadPointer? Surface { get; init; }

    public AutoCadPointer? Aerial { get; init; }

    public IReadOnlyList<AutoCadRecordLayer>? Records { get; init; }

    public AutoCadPointer? Linework { get; init; }

    public required PartReadiness ContoursReadiness { get; init; }

    public required PartReadiness SurfaceReadiness { get; init; }

    public required PartReadiness AerialReadiness { get; init; }

    public PartReadiness RecordsReadiness { get; init; } = PartReadiness.Unstated;

    public PartReadiness LineworkReadiness { get; init; } = PartReadiness.Unstated;

    public bool FromThree { get; init; }

    public IReadOnlyDictionary<AutoCadPart, string> LeftOut { get; init; } = new Dictionary<AutoCadPart, string>();

    public IReadOnlyList<string> RecordsLeftOut { get; init; } = [];
}

public sealed class AutoCadBlockRead
{
    public required BundleManifest Manifest { get; init; }

    public AutoCadBlock? Block { get; init; }

    public string Refusal { get; init; } = string.Empty;
}

public static class AutoCadBlockReader
{
    public const string HostKey = "autocad";

    public static AutoCadBlockRead Read(BundleManifest? manifest, string? manifestText)
    {
        if (AutoCadThreeView.IsThree(manifest))
        {
            return AutoCadThreeView.Read(manifest!);
        }

        return manifestText is not null
            ? Read(manifestText)
            : new AutoCadBlockRead { Manifest = manifest ?? new BundleManifest(), Refusal = LocalBundleImport.NotABundle };
    }

    public static AutoCadBlockRead Read(string manifestJson)
    {
        BundleManifest manifest = BundleManifestReader.Parse(manifestJson);
        if (!manifest.IsValid)
        {
            return new AutoCadBlockRead { Manifest = manifest, Refusal = manifest.Error };
        }

        using JsonDocument document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.Object("hosts")?.Object(HostKey) is not { } block)
        {
            return new AutoCadBlockRead
            {
                Manifest = manifest,
                Refusal = $"This bundle has no AutoCAD block, so there is nothing in it this plugin can place. Bundles "
                    + "made before AutoCAD import existed (manifest 1.18.0) carry none; prepare the order again "
                    + "from your vault at mantle.place/vault to get one.",
            };
        }

        JsonElement? readiness = block.Object("readiness");

        return new AutoCadBlockRead
        {
            Manifest = manifest,
            Block = new AutoCadBlock
            {
                FileFrame = ReadFrame(block.Object("file_frame")),
                Contours = ReadPointer(block.Object("contours")),
                Surface = ReadPointer(block.Object("surface")),
                Aerial = ReadPointer(block.Object("aerial")),
                Records = ReadRecords(block.Object("records")),
                Linework = ReadPointer(block.Object("linework")),
                ContoursReadiness = ReadVerdict(readiness?.Object("contours")),
                SurfaceReadiness = ReadVerdict(readiness?.Object("surface")),
                AerialReadiness = ReadVerdict(readiness?.Object("aerial")),
                RecordsReadiness = ReadVerdict(readiness?.Object("records")),
                LineworkReadiness = ReadVerdict(readiness?.Object("linework")),
            },
        };
    }

    private static AutoCadFileFrame ReadFrame(JsonElement? frame)
    {
        if (frame is not { } f)
        {
            return AutoCadFileFrame.None;
        }

        JsonElement? origin = f.Object("origin");
        return new AutoCadFileFrame(
            f.Str("type"),
            f.Str("crs"),
            f.Str("base_crs"),
            origin?.OptionalDouble("easting"),
            origin?.OptionalDouble("northing"),
            origin?.Str("linear_unit") ?? string.Empty,
            f.Str("horizontal_unit"),
            f.Str("vertical_unit"));
    }

    private static AutoCadPointer? ReadPointer(JsonElement? pointer)
    {
        if (pointer is not { } p)
        {
            return null;
        }

        AerialInsertion? insertion = null;
        if (p.Object("insertion") is { } i
            && i.OptionalDouble("x") is { } x
            && i.OptionalDouble("y") is { } y
            && i.OptionalDouble("width") is { } width
            && i.OptionalDouble("height") is { } height
            && i.OptionalDouble("rotation_deg") is { } rotation)
        {
            insertion = new AerialInsertion(x, y, width, height, rotation);
        }

        return new AutoCadPointer(
            p.Str("path"),
            p.Str("sha256"),
            p.Str("format"),
            p.Str("horizontal_frame"),
            p.Str("horizontal_units"),
            p.Str("vertical_units"),
            p.Str("vertical_reference"),
            p.Str("vertical_datum"),
            p.OptionalInt("width") ?? 0,
            p.OptionalInt("height") ?? 0,
            insertion);
    }

    private static List<AutoCadRecordLayer>? ReadRecords(JsonElement? records)
    {
        if (records is not { } r)
        {
            return null;
        }

        List<AutoCadRecordLayer> layers = [];
        if (r.TryGetProperty("layers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement layer in list.EnumerateArray())
            {
                if (layer.ValueKind == JsonValueKind.Object && ReadPointer(layer) is { } pointer)
                {
                    layers.Add(new AutoCadRecordLayer(layer.Str("name"), pointer));
                }
            }
        }

        return layers;
    }

    private static PartReadiness ReadVerdict(JsonElement? verdict)
    {
        if (verdict is not { } v || v.OptionalBool("present") is not { } present)
        {
            return PartReadiness.Unstated;
        }

        return new PartReadiness(true, present, v.OptionalStr("reason"), v.OptionalStr("requires"));
    }
}
