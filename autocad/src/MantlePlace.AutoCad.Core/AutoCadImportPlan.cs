using System.Globalization;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad;

/// <summary>The three parts of an AutoCAD order (format.md §6.9).</summary>
public enum AutoCadPart
{
    /// <summary>The published contour linework, placed as polylines.</summary>
    Contours,

    /// <summary>The terrain source for a Civil 3D surface.</summary>
    Surface,

    /// <summary>The aerial image, placed at its published insertion.</summary>
    Aerial,
}

/// <summary>The layers an import puts its content on. Names are spelled out (root CLAUDE.md, naming).</summary>
public static class AutoCadLayers
{
    /// <summary>The contours' layer.</summary>
    public const string Contours = "MantlePlace-Contours";

    /// <summary>The aerial image's layer.</summary>
    public const string Aerial = "MantlePlace-Aerial";
}

/// <summary>
/// What the shell observed of the drawing's geographic location: whether one is set, the coordinate
/// system it names in AutoCAD's own words, and the EPSG code AutoCAD resolves that to, if any.
/// </summary>
/// <param name="IsSet">Whether the drawing has a geographic location.</param>
/// <param name="CoordinateSystem">The coordinate system the location names, as AutoCAD names it.</param>
/// <param name="Epsg">The EPSG code AutoCAD gives for it, or <c>null</c> when it gives none.</param>
public sealed record DrawingGeolocation(bool IsSet, string CoordinateSystem, int? Epsg)
{
    /// <summary>A drawing with no geographic location.</summary>
    public static DrawingGeolocation None { get; } = new(false, string.Empty, null);

    /// <summary>A drawing whose geographic location names <paramref name="coordinateSystem"/>.</summary>
    public static DrawingGeolocation Located(string coordinateSystem, int? epsg) => new(true, coordinateSystem ?? string.Empty, epsg);
}

/// <summary>One part of the plan: placed, or skipped with the sentence that says why.</summary>
public sealed class PlannedPart
{
    /// <summary>Which part.</summary>
    public required AutoCadPart Kind { get; init; }

    /// <summary>Whether the import places it.</summary>
    public required bool Place { get; init; }

    /// <summary>Whether the bundle ships it: its verdict says present and the block points at it.</summary>
    public required bool Present { get; init; }

    /// <summary>The verdict's own <c>present</c>.</summary>
    public required bool Ready { get; init; }

    /// <summary>The verdict's stated reason, verbatim, or <c>null</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>The host part the verdict names, or <c>null</c>.</summary>
    public string? Requires { get; init; }

    /// <summary>What the user is told about this part.</summary>
    public required string Sentence { get; init; }

    /// <summary>The archive path, or empty when the block names none.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The digest the file is checked against before it is placed.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>The pointer's format word.</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>The file's horizontal frame, as stated.</summary>
    public string HorizontalFrame { get; init; } = string.Empty;

    /// <summary>The file's X/Y unit token, from its pointer.</summary>
    public string HorizontalUnits { get; init; } = string.Empty;

    /// <summary>The file's height unit token, from its pointer.</summary>
    public string VerticalUnits { get; init; } = string.Empty;

    /// <summary>The layer it goes on, when placed.</summary>
    public string Layer { get; init; } = string.Empty;

    /// <summary>The aerial's insertion, verbatim, when the block publishes one.</summary>
    public AerialInsertion? Insertion { get; init; }
}

/// <summary>What one press does to the drawing: refuse, or place these parts and skip those, saying why.</summary>
public sealed class AutoCadImportPlan
{
    private static readonly AutoCadPart[] Order = [AutoCadPart.Contours, AutoCadPart.Surface, AutoCadPart.Aerial];

    /// <summary>The order this bundle is, still named on a refusal (HPS-37).</summary>
    public string OrderId { get; init; } = string.Empty;

    /// <summary>The packaging job.</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>Why nothing is imported, or empty.</summary>
    public string Refusal { get; init; } = string.Empty;

    /// <summary>Whether the import refuses as a whole.</summary>
    public bool IsRefused => Refusal.Length > 0;

    /// <summary>The block's file frame.</summary>
    public AutoCadFileFrame Frame { get; init; } = AutoCadFileFrame.None;

    /// <summary>The unit the drawing's X and Y are in for this order: the block's, never the drawing's.</summary>
    public string HorizontalUnit => Frame.HorizontalUnit;

    /// <summary>The unit the placed heights are in: the block's.</summary>
    public string VerticalUnit => Frame.VerticalUnit;

    /// <summary>The three parts, in the order they are placed. Empty on a refusal.</summary>
    public IReadOnlyList<PlannedPart> Parts { get; init; } = [];

    /// <summary>What the user is told beside the parts: where the order lands, in which units.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>The parts not placed.</summary>
    public IEnumerable<PlannedPart> Skipped => Parts.Where(p => !p.Place);

    /// <summary>One part of the plan; on a refusal, a skipped part that says the import refused.</summary>
    public PlannedPart Part(AutoCadPart kind) =>
        Parts.FirstOrDefault(p => p.Kind == kind)
        ?? new PlannedPart { Kind = kind, Place = false, Present = false, Ready = false, Sentence = Refusal };

    /// <summary>The whole plan as the text the shell shows: before an import, or with nothing yet run.</summary>
    public string Summary() => Summary(null);

    /// <summary>
    /// The whole plan as the text the shell shows after an import, each part's sentence replaced by what
    /// running it said, where it said anything (a count placed, or why a placed part failed).
    /// </summary>
    public string Summary(IReadOnlyDictionary<AutoCadPart, string>? outcomes)
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"Mantle Place order {OrderId}");
        if (IsRefused)
        {
            text.Append(": ").Append(Refusal);
            return text.ToString();
        }

        text.AppendLine(".");
        foreach (string notice in Notices)
        {
            text.AppendLine(notice);
        }

        foreach (AutoCadPart kind in Order)
        {
            text.AppendLine(outcomes is not null && outcomes.TryGetValue(kind, out string? outcome) ? outcome : Part(kind).Sentence);
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>Finds a part's file in the bundle zip by its manifest path, by the shared index's rule (HPS-32).</summary>
public static class AutoCadBundleEntries
{
    /// <summary>The archive's own spelling of <paramref name="manifestPath"/>, or <c>null</c> when it is not carried unambiguously.</summary>
    public static string? Resolve(IEnumerable<string> entryNames, string manifestPath) =>
        new BundleEntryIndex(entryNames).Resolve(manifestPath);
}

/// <summary>
/// Decides what importing an order into a drawing does, from the <c>hosts.autocad</c> block alone. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Every placement value comes from the block and none is derived</b> (root CLAUDE.md, the
/// boundary that keeps this client thin). The drawing's coordinates are the files' coordinates, in the
/// block's units; the drawing's own unit setting is not read. The aerial goes in at its published
/// insertion. A file the block does not show to be in its frame is skipped, never reprojected (HPS-53).
/// </para>
/// <para>
/// The one fact taken from the drawing is its geographic location, and only to refuse: a location in
/// another coordinate system than the order's means the order's coordinates mean something else in this
/// drawing, so nothing is imported and the refusal names both. A drawing with none set is placed at the
/// order's coordinates, and a notice says so; the plugin never sets the location for the user (the Civil
/// 3D spike's rule for the drawing zone, autocad/docs/api-record.md). A local frame's files are offsets
/// in no coordinate system, so no geographic location agrees with them.
/// </para>
/// <para>
/// Parts are decided apart: an absent one is skipped with its stated reason, echoed and never matched on
/// (format.md §6.1), and the others are planned as usual. The surface is Civil 3D's; plain AutoCAD says it
/// needs Civil 3D from its own verdict, and Civil 3D names it and says building it is not in this plugin yet.
/// </para>
/// </remarks>
public static class AutoCadImportPlanner
{
    /// <summary>The one host part a surface verdict names that this plugin knows.</summary>
    public const string Civil3dPart = "civil3d";

    /// <summary>The surface format this plugin knows (format.md §6.9).</summary>
    public const string LandXmlFormat = "landxml";

    /// <summary>The aerial format this plugin can insert.</summary>
    public const string AerialPngFormat = "png-rgb-8bit";

    private const string AbsoluteProjected = "absolute_projected";
    private const string LocalEnu = "local_enu";
    private const string AbsoluteHeights = "absolute";
    private const string EpsgPrefix = "EPSG:";

    /// <summary>Reads <paramref name="manifestJson"/> and plans it. Never throws.</summary>
    public static AutoCadImportPlan Plan(string manifestJson, DrawingGeolocation drawing, bool civil3dLoaded)
        => Plan(AutoCadBlockReader.Read(manifestJson), drawing, civil3dLoaded);

    /// <summary>Plans an already read manifest.</summary>
    public static AutoCadImportPlan Plan(AutoCadBlockRead read, DrawingGeolocation drawing, bool civil3dLoaded)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(drawing);

        string orderId = read.Manifest.OrderId;
        string jobId = read.Manifest.JobId;

        if (read.Block is not { } block)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Refusal = read.Refusal };
        }

        AutoCadFileFrame frame = block.FileFrame;
        if (FrameProblem(frame) is { } frameProblem)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Frame = frame, Refusal = frameProblem };
        }

        if (LocationRefusal(frame, drawing) is { } refusal)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Frame = frame, Refusal = refusal };
        }

        return new AutoCadImportPlan
        {
            OrderId = orderId,
            JobId = jobId,
            Frame = frame,
            Parts =
            [
                PlanContours(block, frame),
                PlanSurface(block, civil3dLoaded),
                PlanAerial(block, frame),
            ],
            Notices = [LocationNotice(frame, drawing), UnitsNotice(frame)],
        };
    }

    /// <summary>What the user is told about a placed part whose placing failed: the part, and why.</summary>
    public static string FailedSentence(AutoCadPart kind, string why) => $"{Label(kind)}: not imported, because {why}";

    /// <summary>What the user is told about placed contours, with the count that went in.</summary>
    public static string ContoursPlacedSentence(PlannedPart part, int placed)
    {
        ArgumentNullException.ThrowIfNull(part);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Contours: {placed} polylines placed on {part.Layer}, each at its height in {UnitWords(part.VerticalUnits)}.");
    }

    /// <summary>The words a unit token is shown in, or <c>null</c> for a token this plugin does not know.</summary>
    public static string? UnitWords(string token) => token switch
    {
        "m" => "metres",
        "ft" => "international feet",
        "ftUS" => "US survey feet",
        _ => null,
    };

    private static string? FrameProblem(AutoCadFileFrame frame)
    {
        const string Lead = "The order's AutoCAD block states a frame this plugin cannot place in";

        if (UnitWords(frame.HorizontalUnit) is null)
        {
            return $"{Lead}: its X/Y unit is \"{frame.HorizontalUnit}\", which this plugin does not know, so nothing was imported.";
        }

        if (UnitWords(frame.VerticalUnit) is null)
        {
            return $"{Lead}: its height unit is \"{frame.VerticalUnit}\", which this plugin does not know, so nothing was imported.";
        }

        return frame.Type switch
        {
            AutoCadFileFrame.Projected when EpsgOf(frame.Crs) is null =>
                $"{Lead}: it names the coordinate system \"{frame.Crs}\", which is not an EPSG code, so nothing was imported.",
            AutoCadFileFrame.Projected => null,
            AutoCadFileFrame.Local when frame.BaseCrs.Length == 0 || frame.OriginEasting is null || frame.OriginNorthing is null =>
                $"{Lead}: it is a local frame with no complete origin, so nothing was imported.",
            AutoCadFileFrame.Local => null,
            _ => $"{Lead}: its type is \"{frame.Type}\", which this plugin does not know, so nothing was imported.",
        };
    }

    private static int? EpsgOf(string crs) =>
        crs.StartsWith(EpsgPrefix, StringComparison.Ordinal)
        && int.TryParse(crs.AsSpan(EpsgPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
            ? code
            : null;

    private static string DrawingSetting(DrawingGeolocation drawing) =>
        drawing.Epsg is { } epsg
            ? string.Create(CultureInfo.InvariantCulture, $"{drawing.CoordinateSystem} (EPSG:{epsg})")
            : drawing.CoordinateSystem;

    private static string? LocationRefusal(AutoCadFileFrame frame, DrawingGeolocation drawing)
    {
        if (!drawing.IsSet)
        {
            return null;
        }

        const string Remedy = "Nothing was imported. Import into a drawing whose geographic location is the order's, or into one with none set.";

        if (string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal))
        {
            return $"This drawing's geographic location is {DrawingSetting(drawing)}, and this order's files are offsets about a "
                + $"local origin on {frame.BaseCrs}, in no coordinate system, so the drawing's location cannot be theirs. "
                + "Nothing was imported. Import it into a drawing with no geographic location set.";
        }

        int orderEpsg = EpsgOf(frame.Crs)!.Value;
        if (drawing.Epsg is not { } drawingEpsg)
        {
            return $"This drawing's geographic location is {drawing.CoordinateSystem}, which AutoCAD gives no EPSG code for, so "
                + $"it cannot be shown to be the order's {frame.Crs}. {Remedy}";
        }

        if (drawingEpsg != orderEpsg)
        {
            return $"This drawing's geographic location is {DrawingSetting(drawing)}, and the order is in {frame.Crs}: placing it "
                + $"here would put the site in the wrong place. {Remedy}";
        }

        return null;
    }

    private static string LocationNotice(AutoCadFileFrame frame, DrawingGeolocation drawing)
    {
        if (string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal))
        {
            string origin = string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.OriginEasting} E, {frame.OriginNorthing} N {frame.OriginLinearUnit} on {frame.BaseCrs}");
            return $"This drawing has no geographic location. The order's files are offsets about a local origin ({origin}), "
                + "and are placed as those offsets.";
        }

        return drawing.IsSet
            ? $"This drawing's geographic location, {DrawingSetting(drawing)}, is the order's coordinate system, so the order is placed at its own coordinates."
            : $"This drawing has no geographic location, so the order is placed at its own coordinates, in {frame.Crs}. "
                + "Set the drawing's geographic location to that coordinate system to tie it to the map; the plugin does not set it for you.";
    }

    private static string UnitsNotice(AutoCadFileFrame frame) =>
        $"Coordinates are in {UnitWords(frame.HorizontalUnit)} and heights in {UnitWords(frame.VerticalUnit)}, as the order states; "
        + "the drawing's own unit setting was not consulted.";

    private static string Label(AutoCadPart kind) => kind switch
    {
        AutoCadPart.Contours => "Contours",
        AutoCadPart.Surface => "Civil 3D surface",
        _ => "Aerial image",
    };

    /// <summary>The part's verdict and pointer, before anything particular to the part is decided.</summary>
    private static PlannedPart? Absent(AutoCadPart kind, PartReadiness verdict, AutoCadPointer? pointer)
    {
        string label = Label(kind);

        if (!verdict.Stated)
        {
            return Skip(kind, verdict, pointer, $"{label}: not imported. The bundle states no verdict for it, so the plugin does not assume it is there.");
        }

        if (!verdict.Present)
        {
            string clause = ReadinessReasons.ClauseFor(verdict.Reason) ?? "the bundle gives no reason";
            return Skip(kind, verdict, pointer, $"{label}: not in this bundle: {clause}.");
        }

        return pointer is null
            ? Skip(kind, verdict, pointer, $"{label}: not imported. The bundle says it is ready but names no file for it.")
            : null;
    }

    private static PlannedPart Skip(AutoCadPart kind, PartReadiness verdict, AutoCadPointer? pointer, string sentence) => new()
    {
        Kind = kind,
        Place = false,
        Present = verdict.Present && pointer is not null,
        Ready = verdict.Present,
        Reason = verdict.Reason,
        Requires = verdict.Requires,
        Sentence = sentence,
        Path = pointer?.Path ?? string.Empty,
        Sha256 = pointer?.Sha256 ?? string.Empty,
        Format = pointer?.Format ?? string.Empty,
        HorizontalFrame = pointer?.HorizontalFrame ?? string.Empty,
        HorizontalUnits = pointer?.HorizontalUnits ?? string.Empty,
        VerticalUnits = pointer?.VerticalUnits ?? string.Empty,
        Insertion = pointer?.Insertion,
    };

    private static PlannedPart Placed(AutoCadPart kind, PartReadiness verdict, AutoCadPointer pointer, string layer, string sentence) => new()
    {
        Kind = kind,
        Place = true,
        Present = true,
        Ready = true,
        Reason = verdict.Reason,
        Requires = verdict.Requires,
        Sentence = sentence,
        Path = pointer.Path,
        Sha256 = pointer.Sha256,
        Format = pointer.Format,
        HorizontalFrame = pointer.HorizontalFrame,
        HorizontalUnits = pointer.HorizontalUnits,
        VerticalUnits = pointer.VerticalUnits,
        Layer = layer,
        Insertion = pointer.Insertion,
    };

    /// <summary>The frame word a file in this block's frame states.</summary>
    private static string FrameWord(AutoCadFileFrame frame) =>
        string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal) ? LocalEnu : AbsoluteProjected;

    /// <summary>Why a file is not shown to be in the block's frame and units, or <c>null</c>.</summary>
    private static string? OutOfFrame(AutoCadPointer pointer, AutoCadFileFrame frame, bool hasHeights)
    {
        if (!string.Equals(pointer.HorizontalFrame, FrameWord(frame), StringComparison.Ordinal))
        {
            return $"its file states the frame \"{pointer.HorizontalFrame}\", not this order's \"{FrameWord(frame)}\", and the plugin does not reproject (HPS-53)";
        }

        if (UnitWords(pointer.HorizontalUnits) is null)
        {
            return $"its X/Y unit is \"{pointer.HorizontalUnits}\", which this plugin does not know";
        }

        if (!string.Equals(pointer.HorizontalUnits, frame.HorizontalUnit, StringComparison.Ordinal))
        {
            return $"its X/Y unit is {pointer.HorizontalUnits}, not the frame's {frame.HorizontalUnit}, and the plugin does not convert";
        }

        if (!hasHeights)
        {
            return null;
        }

        if (UnitWords(pointer.VerticalUnits) is null)
        {
            return $"its height unit is \"{pointer.VerticalUnits}\", which this plugin does not know";
        }

        if (!string.Equals(pointer.VerticalUnits, frame.VerticalUnit, StringComparison.Ordinal))
        {
            return $"its height unit is {pointer.VerticalUnits}, not the frame's {frame.VerticalUnit}, and the plugin does not convert";
        }

        return string.Equals(pointer.VerticalReference, AbsoluteHeights, StringComparison.Ordinal)
            ? null
            : $"its heights are \"{pointer.VerticalReference}\", not absolute";
    }

    private static PlannedPart PlanContours(AutoCadBlock block, AutoCadFileFrame frame)
    {
        PartReadiness verdict = block.ContoursReadiness;
        AutoCadPointer? pointer = block.Contours;

        if (Absent(AutoCadPart.Contours, verdict, pointer) is { } absent)
        {
            return absent;
        }

        if (OutOfFrame(pointer!, frame, hasHeights: true) is { } why)
        {
            return Skip(AutoCadPart.Contours, verdict, pointer, $"Contours: not imported, because {why}.");
        }

        return Placed(
            AutoCadPart.Contours,
            verdict,
            pointer!,
            AutoCadLayers.Contours,
            $"Contours: placed as polylines on {AutoCadLayers.Contours}, each at its height in {UnitWords(pointer!.VerticalUnits)} ({pointer.VerticalDatum}).");
    }

    private static PlannedPart PlanSurface(AutoCadBlock block, bool civil3dLoaded)
    {
        PartReadiness verdict = block.SurfaceReadiness;
        AutoCadPointer? pointer = block.Surface;
        string label = Label(AutoCadPart.Surface);

        // The host part comes first: a plain AutoCAD reader answers from the verdict's `requires` whether
        // or not the file shipped, so an absent surface still says what it would need (format.md §6.9).
        string? requires = verdict.Requires;
        bool known = string.Equals(requires, Civil3dPart, StringComparison.Ordinal);
        string? unavailable = requires is null
            ? null
            : !known
                ? $"it needs \"{requires}\", which this plugin does not know"
                : civil3dLoaded ? null : "it needs Civil 3D, and this is plain AutoCAD";

        if (Absent(AutoCadPart.Surface, verdict, pointer) is { } absent)
        {
            return unavailable is null
                ? absent
                : Skip(AutoCadPart.Surface, verdict, pointer, $"{absent.Sentence} {char.ToUpperInvariant(unavailable[0])}{unavailable[1..]}.");
        }

        if (unavailable is not null)
        {
            return Skip(AutoCadPart.Surface, verdict, pointer, $"{label}: not imported, because {unavailable}.");
        }

        if (!string.Equals(pointer!.Format, LandXmlFormat, StringComparison.Ordinal))
        {
            return Skip(AutoCadPart.Surface, verdict, pointer, $"{label}: not imported, because its format \"{pointer.Format}\" is one this plugin does not know.");
        }

        return Skip(
            AutoCadPart.Surface,
            verdict,
            pointer,
            $"{label}: not imported. Building a surface from {pointer.Path} is not built into this plugin yet; the file is in the bundle.");
    }

    private static PlannedPart PlanAerial(AutoCadBlock block, AutoCadFileFrame frame)
    {
        PartReadiness verdict = block.AerialReadiness;
        AutoCadPointer? pointer = block.Aerial;
        string label = Label(AutoCadPart.Aerial);

        if (Absent(AutoCadPart.Aerial, verdict, pointer) is { } absent)
        {
            return absent;
        }

        if (!string.Equals(pointer!.Format, AerialPngFormat, StringComparison.Ordinal))
        {
            return Skip(AutoCadPart.Aerial, verdict, pointer, $"{label}: not imported, because its format \"{pointer.Format}\" is one this plugin does not know.");
        }

        if (pointer.Insertion is not { } insertion)
        {
            return Skip(AutoCadPart.Aerial, verdict, pointer, $"{label}: not imported. The bundle publishes no insertion for it, and the plugin does not work one out.");
        }

        if (!(insertion.Width > 0 && insertion.Height > 0))
        {
            return Skip(AutoCadPart.Aerial, verdict, pointer, $"{label}: not imported. Its published insertion has no ground size.");
        }

        return Placed(
            AutoCadPart.Aerial,
            verdict,
            pointer,
            AutoCadLayers.Aerial,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: placed on {AutoCadLayers.Aerial}, its lower-left corner at {insertion.X}, {insertion.Y}, {insertion.Width} by {insertion.Height} {UnitWords(frame.HorizontalUnit)}, rotated {insertion.RotationDeg} degrees."));
    }
}
