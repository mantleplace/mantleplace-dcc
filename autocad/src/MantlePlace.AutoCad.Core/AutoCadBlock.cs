using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad;

/// <summary>The frame every file the AutoCAD block points at is in (<c>hosts.autocad.file_frame</c>, format.md §4.2).</summary>
/// <param name="Type"><c>projected</c> or <c>local</c>, as published.</param>
/// <param name="Crs">The files' CRS on a projected frame (<c>EPSG:2231</c>); empty on a local one, which names none.</param>
/// <param name="BaseCrs">The grid a local frame's origin is on; empty on a projected frame.</param>
/// <param name="OriginEasting">A local frame's origin, in its own unit; <c>null</c> when not stated.</param>
/// <param name="OriginNorthing">A local frame's origin, in its own unit; <c>null</c> when not stated.</param>
/// <param name="OriginLinearUnit">The origin's own unit, kept apart from the files'.</param>
/// <param name="HorizontalUnit">The files' X/Y unit token.</param>
/// <param name="VerticalUnit">The files' height unit token.</param>
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
    /// <summary>The type of a frame whose files are in a named CRS.</summary>
    public const string Projected = "projected";

    /// <summary>The type of a frame whose files are offsets about an origin.</summary>
    public const string Local = "local";

    /// <summary>A frame the block did not state.</summary>
    public static AutoCadFileFrame None { get; } = new(string.Empty, string.Empty, string.Empty, null, null, string.Empty, string.Empty, string.Empty);
}

/// <summary>Where and how the aerial goes in the drawing, exactly as published (format.md §6.9).</summary>
/// <param name="X">The image's lower-left corner, in the file frame's X/Y unit.</param>
/// <param name="Y">The image's lower-left corner, in the file frame's X/Y unit.</param>
/// <param name="Width">The ground width of the whole image, not of a pixel.</param>
/// <param name="Height">The ground height of the whole image.</param>
/// <param name="RotationDeg">The image's rotation, in degrees.</param>
public sealed record AerialInsertion(double X, double Y, double Width, double Height, double RotationDeg);

/// <summary>One part's readiness verdict (format.md §6.1): present, and when not, the stated reason.</summary>
/// <param name="Stated">Whether the block states a verdict for the part at all.</param>
/// <param name="Present">The verdict.</param>
/// <param name="Reason">The stated reason, verbatim; <c>null</c> when none is stated.</param>
/// <param name="Requires">The host part that can place the file, where the verdict names one (the surface's).</param>
public sealed record PartReadiness(bool Stated, bool Present, string? Reason, string? Requires)
{
    /// <summary>A part the block gives no verdict for.</summary>
    public static PartReadiness Unstated { get; } = new(false, false, null, null);
}

/// <summary>One pointer of the AutoCAD block: a file and the frame and units it states for itself.</summary>
/// <param name="Path">The archive path.</param>
/// <param name="Sha256">The file's published digest.</param>
/// <param name="Format">The pointer's own format word (the surface's and the aerial's); empty for the contours.</param>
/// <param name="HorizontalFrame"><c>absolute_projected</c> or <c>local_enu</c>, as stated.</param>
/// <param name="HorizontalUnits">The X/Y unit token, from the pointer.</param>
/// <param name="VerticalUnits">The height unit token, from the pointer.</param>
/// <param name="VerticalReference">The heights' reference, as stated.</param>
/// <param name="VerticalDatum">The heights' datum, as stated.</param>
/// <param name="PixelWidth">The aerial's pixel width; zero for the others.</param>
/// <param name="PixelHeight">The aerial's pixel height; zero for the others.</param>
/// <param name="Insertion">The aerial's insertion; <c>null</c> for the others, or when not published.</param>
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

/// <summary>One public-record layer the block lists (format.md §6.10): its name, and its file's pointer.</summary>
/// <param name="Name">The record layer's name, verbatim (<c>parcels</c>).</param>
/// <param name="Pointer">The layer's file, with the frame and units it states for itself.</param>
public sealed record AutoCadRecordLayer(string Name, AutoCadPointer Pointer);

/// <summary>The <c>hosts.autocad</c> block, read and not interpreted.</summary>
public sealed class AutoCadBlock
{
    /// <summary>The block's file frame.</summary>
    public required AutoCadFileFrame FileFrame { get; init; }

    /// <summary>The contours pointer, or <c>null</c> when the block carries none.</summary>
    public AutoCadPointer? Contours { get; init; }

    /// <summary>The surface pointer, or <c>null</c>.</summary>
    public AutoCadPointer? Surface { get; init; }

    /// <summary>The aerial pointer, or <c>null</c>.</summary>
    public AutoCadPointer? Aerial { get; init; }

    /// <summary>The public-record layers, in the order the block lists them, or <c>null</c> when it carries no <c>records</c> (from 1.19.0).</summary>
    public IReadOnlyList<AutoCadRecordLayer>? Records { get; init; }

    /// <summary>The site linework pointer, or <c>null</c> (from 1.19.0).</summary>
    public AutoCadPointer? Linework { get; init; }

    /// <summary>The contours' verdict.</summary>
    public required PartReadiness ContoursReadiness { get; init; }

    /// <summary>The surface's verdict.</summary>
    public required PartReadiness SurfaceReadiness { get; init; }

    /// <summary>The aerial's verdict.</summary>
    public required PartReadiness AerialReadiness { get; init; }

    /// <summary>The public-record layers' verdict, one for the set; unstated on a block from before 1.19.0.</summary>
    public PartReadiness RecordsReadiness { get; init; } = PartReadiness.Unstated;

    /// <summary>The site linework's verdict; unstated on a block from before 1.19.0.</summary>
    public PartReadiness LineworkReadiness { get; init; } = PartReadiness.Unstated;
}

/// <summary>What reading a manifest for AutoCAD gave: the shared facts, and this host's block or why there is none.</summary>
public sealed class AutoCadBlockRead
{
    /// <summary>The shared reader's view: the version gate, the order identity, the materialisation verdict.</summary>
    public required BundleManifest Manifest { get; init; }

    /// <summary>The block, or <c>null</c> when <see cref="Refusal"/> says why there is none to plan from.</summary>
    public AutoCadBlock? Block { get; init; }

    /// <summary>Why the manifest cannot be imported here, or empty.</summary>
    public string Refusal { get; init; } = string.Empty;
}

/// <summary>
/// Reads <c>hosts.autocad</c> (format.md §6.9). Pure: text in, the block's values out, verbatim.
/// </summary>
/// <remarks>
/// <para>
/// The manifest goes through the shared <see cref="BundleManifestReader"/> first, for what every host
/// reads alike: the version gate (HPS-31), the order identity a refusal still names (HPS-37) and the
/// materialisation verdict (HPS-47). Its refusal is this reader's refusal. Then the block is read, and
/// nothing else: ⛔ never another host's block, never <c>elevation.contours</c> as a fallback for a missing
/// contours pointer, and never a value worked out from the ones that are there (format.md §4.2).
/// </para>
/// <para>
/// Nothing is checked here beyond shape. Whether a unit is known, a frame agrees or a part can be placed
/// is <see cref="AutoCadImportPlanner"/>'s decision, so the reader can be asked what the bundle said even
/// about a part the plan declines.
/// </para>
/// </remarks>
public static class AutoCadBlockReader
{
    /// <summary>This host's key under <c>hosts</c>, the id the platform knows the plugin by.</summary>
    public const string HostKey = HostIdentity.Id;

    /// <summary>Reads the manifest text. Never throws.</summary>
    public static AutoCadBlockRead Read(string manifestJson)
    {
        BundleManifest manifest = BundleManifestReader.Parse(manifestJson);
        if (!manifest.IsValid)
        {
            return new AutoCadBlockRead { Manifest = manifest, Refusal = manifest.Error };
        }

        // The shared reader has parsed this text already; a second parse cannot fail where the first did not.
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

    /// <summary>The record layers as listed; an entry that is not an object is skipped, and the planner sees only what is there.</summary>
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
