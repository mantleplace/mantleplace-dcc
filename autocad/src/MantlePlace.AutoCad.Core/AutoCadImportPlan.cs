using System.Globalization;
using System.Text;
using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public enum AutoCadPart
{
    Contours,

    Surface,

    Aerial,

    Records,

    Linework,
}

public static class AutoCadLayers
{
    public const string Contours = "MantlePlace-Contours";

    public const string Aerial = "MantlePlace-Aerial";

    public const string Surface = "MantlePlace-Surface";

    public const string Linework = "MantlePlace-Linework";

    public const string RecordsStem = "MantlePlace-Records-";

    private const string RefusedInLayerName = "<>/\\\":;?*|,=`";

    public static string Record(string name) => RecordsStem + Safe(name);

    public static string LineworkLayer(string fileLayer) =>
        string.IsNullOrEmpty(fileLayer) || string.Equals(fileLayer, "0", StringComparison.Ordinal)
            ? Linework
            : $"{Linework}-{Safe(fileLayer)}";

    private static string Safe(string name) =>
        string.Concat((name ?? string.Empty).Select(c => char.IsControl(c) || RefusedInLayerName.Contains(c, StringComparison.Ordinal) ? '-' : c));
}

public sealed record PlannedFile(
    string Name,
    string Path,
    string Sha256,
    string HorizontalFrame,
    string HorizontalUnits,
    string VerticalUnits,
    string Layer);

public sealed record DrawingGeolocation(bool IsSet, string CoordinateSystem, int? Epsg)
{
    public static DrawingGeolocation None { get; } = new(false, string.Empty, null);

    public static DrawingGeolocation Located(string coordinateSystem, int? epsg) => new(true, coordinateSystem ?? string.Empty, epsg);
}

public sealed record DrawingZone(string Code, int? Epsg)
{
    public const string NoZoneCode = ".";

    public static DrawingZone None { get; } = new(NoZoneCode, null);

    public bool IsSet => !string.IsNullOrWhiteSpace(Code) && !string.Equals(Code, NoZoneCode, StringComparison.Ordinal);
}

public sealed class PlannedPart
{
    public required AutoCadPart Kind { get; init; }

    public required bool Place { get; init; }

    public required bool Present { get; init; }

    public required bool Ready { get; init; }

    public bool VerdictStated { get; init; }

    public string? Reason { get; init; }

    public string? Requires { get; init; }

    public required string Sentence { get; init; }

    public string Path { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string HorizontalFrame { get; init; } = string.Empty;

    public string HorizontalUnits { get; init; } = string.Empty;

    public string VerticalUnits { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public AerialInsertion? Insertion { get; init; }

    public IReadOnlyList<PlannedFile> Files { get; init; } = [];

    public IReadOnlyList<string> NotInBundle { get; init; } = [];
}

public sealed class AutoCadImportPlan
{
    private static readonly AutoCadPart[] Order = [AutoCadPart.Contours, AutoCadPart.Surface, AutoCadPart.Aerial, AutoCadPart.Records, AutoCadPart.Linework];

    public static IReadOnlyList<string> ThreeFiles { get; } =
    [
        "Elevation/Contours.dxf",
        "Elevation/Terrain.landxml",
        "Imagery/Imagery.tif",
        "Records/Parcels.dxf",
        "Records/Zoning.dxf",
        "Records/Floodplain.dxf",
        "Records/Wetlands.dxf",
        "Records/Soils.dxf",
        "Records/Heritage.dxf",
        "Records/Conservation.dxf",
        "Records/Hydrography.dxf",
        "Records/Contamination.dxf",
        "Site/Site.dxf",
    ];

    public string OrderId { get; init; } = string.Empty;

    public string JobId { get; init; } = string.Empty;

    public string Refusal { get; init; } = string.Empty;

    public bool IsRefused => Refusal.Length > 0;

    public AutoCadFileFrame Frame { get; init; } = AutoCadFileFrame.None;

    public bool FromThree { get; init; }

    public string HorizontalUnit => Frame.HorizontalUnit;

    public string VerticalUnit => Frame.VerticalUnit;

    public IReadOnlyList<PlannedPart> Parts { get; init; } = [];

    public IReadOnlyList<string> Notices { get; init; } = [];

    public IEnumerable<PlannedPart> Skipped => Parts.Where(p => !p.Place);

    public PlannedPart Part(AutoCadPart kind) =>
        Parts.FirstOrDefault(p => p.Kind == kind)
        ?? new PlannedPart { Kind = kind, Place = false, Present = false, Ready = false, Sentence = Refusal };

    public string Summary() => Summary(null);

    public string Summary(IReadOnlyDictionary<AutoCadPart, string>? outcomes)
    {
        StringBuilder text = new();
        text.Append(OrderId.Length == 0 ? "Mantle Place" : string.Create(CultureInfo.InvariantCulture, $"Mantle Place order {OrderId}"));
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

public static class AutoCadBundleEntries
{
    public static string? Resolve(IEnumerable<string> entryNames, string manifestPath) =>
        new BundleEntryIndex(entryNames).Resolve(manifestPath);
}

public static class AutoCadImportPlanner
{
    public const string Civil3dPart = "civil3d";

    public const string LandXmlFormat = "landxml";

    public const string AerialPngFormat = "png-rgb-8bit";

    private const string AbsoluteProjected = "absolute_projected";
    private const string LocalEnu = "local_enu";
    private const string AbsoluteHeights = "absolute";
    private const string EpsgPrefix = "EPSG:";

    public static AutoCadImportPlan Plan(string manifestJson, DrawingGeolocation drawing, DrawingZone? civil3d)
        => Plan(AutoCadBlockReader.Read(manifestJson), drawing, civil3d);

    public static AutoCadImportPlan Plan(AutoCadBlockRead read, DrawingGeolocation drawing, DrawingZone? civil3d)
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
        if (FrameProblem(frame, block.FromThree) is { } frameProblem)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Frame = frame, Refusal = frameProblem };
        }

        if (LocationRefusal(frame, drawing) is { } refusal)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Frame = frame, Refusal = refusal };
        }

        if (civil3d is not null && ZoneRefusal(frame, civil3d) is { } zoneRefusal)
        {
            return new AutoCadImportPlan { OrderId = orderId, JobId = jobId, Frame = frame, Refusal = zoneRefusal };
        }

        List<string> notices = [LocationNotice(frame, drawing)];
        if (civil3d is not null && ZoneNotice(frame, civil3d) is { } zoneNotice)
        {
            notices.Add(zoneNotice);
        }

        notices.Add(UnitsNotice(frame));

        return new AutoCadImportPlan
        {
            OrderId = orderId,
            JobId = jobId,
            Frame = frame,
            FromThree = block.FromThree,
            Parts =
            [
                LeftOut(block, AutoCadPart.Contours) ?? PlanContours(block, frame),
                LeftOut(block, AutoCadPart.Surface) ?? PlanSurface(block, frame, civil3d is not null),
                LeftOut(block, AutoCadPart.Aerial) ?? PlanAerial(block, frame),
                LeftOut(block, AutoCadPart.Records) ?? PlanRecords(block, frame),
                LeftOut(block, AutoCadPart.Linework) ?? PlanLinework(block, frame),
            ],
            Notices = notices,
        };
    }

    public static string FailedSentence(AutoCadPart kind, string why) => $"{Label(kind)}: not imported, because {why}";

    public static string ContoursPlacedSentence(PlannedPart part, int placed)
    {
        ArgumentNullException.ThrowIfNull(part);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Contours: {placed} polylines placed on {part.Layer}, each at its height in {UnitWords(part.VerticalUnits)}.");
    }

    public static string RecordsPlacedSentence(PlannedPart part, int placed, IReadOnlyList<RefusedRecordLayer>? refused = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        refused ??= [];
        List<string> layers = [.. part.Files.Where(f => !refused.Any(r => string.Equals(r.Name, f.Name, StringComparison.Ordinal))).Select(f => f.Layer)];
        string sentence = string.Create(
            CultureInfo.InvariantCulture,
            $"{Label(AutoCadPart.Records)}: {placed} polylines placed on {layers.Count} layers ({string.Join(", ", layers)}).");
        if (refused.Count > 0)
        {
            sentence = $"{sentence} Not placed: {string.Join(" ", refused.Select(r => $"{r.Name} ({r.Why})"))}";
        }

        return WithNotInBundle(sentence, part.NotInBundle);
    }

    internal static string LabelOf(AutoCadPart kind) => Label(kind);

    public static string LineworkPlacedSentence(PlannedPart part, int placed, int layers)
    {
        ArgumentNullException.ThrowIfNull(part);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Label(AutoCadPart.Linework)}: {placed} polylines placed on {layers} layers under {part.Layer}.");
    }

    public static string SurfaceName(string orderId) =>
        "MantlePlace-Surface-" + string.Concat((orderId ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    public static string SurfacePlacedSentence(PlannedPart part, string name, int points)
    {
        ArgumentNullException.ThrowIfNull(part);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Label(AutoCadPart.Surface)}: built as {name}, a TIN surface of {points} points from {part.Path}, on {part.Layer}.");
    }

    public static string? UnitWords(string token) => token switch
    {
        "m" => "metres",
        "ft" => "international feet",
        "ftUS" => "US survey feet",
        _ => null,
    };

    private static string? FrameProblem(AutoCadFileFrame frame, bool fromThree)
    {
        string lead = fromThree
            ? "This bundle's georeferenced frame is one this plugin cannot place in"
            : "The order's AutoCAD block states a frame this plugin cannot place in";

        if (UnitWords(frame.HorizontalUnit) is null)
        {
            return $"{lead}: its X/Y unit is \"{frame.HorizontalUnit}\", which this plugin does not know, so nothing was imported.";
        }

        if (UnitWords(frame.VerticalUnit) is null)
        {
            return $"{lead}: its height unit is \"{frame.VerticalUnit}\", which this plugin does not know, so nothing was imported.";
        }

        return frame.Type switch
        {
            AutoCadFileFrame.Projected when EpsgOf(frame.Crs) is null =>
                $"{lead}: it names the coordinate system \"{frame.Crs}\", which is not an EPSG code, so nothing was imported.",
            AutoCadFileFrame.Projected => null,
            AutoCadFileFrame.Local when frame.BaseCrs.Length == 0 || frame.OriginEasting is null || frame.OriginNorthing is null =>
                $"{lead}: it is a local frame with no complete origin, so nothing was imported.",
            AutoCadFileFrame.Local => null,
            _ => $"{lead}: its type is \"{frame.Type}\", which this plugin does not know, so nothing was imported.",
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

    private static string? ZoneRefusal(AutoCadFileFrame frame, DrawingZone zone)
    {
        if (!zone.IsSet)
        {
            return null;
        }

        const string Remedy = "Nothing was imported. Import into a drawing whose Civil 3D zone is the order's, or into one with none set; "
            + "the plugin does not change the zone.";

        if (string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal))
        {
            return $"This drawing's Civil 3D coordinate zone is {ZoneSetting(zone)}, and this order's files are offsets about a local "
                + $"origin on {frame.BaseCrs}, in no coordinate system, so the drawing's zone cannot be theirs. "
                + "Nothing was imported. Import it into a drawing with no zone set; the plugin does not change the zone.";
        }

        int orderEpsg = EpsgOf(frame.Crs)!.Value;
        if (zone.Epsg is not { } zoneEpsg)
        {
            return $"This drawing's Civil 3D coordinate zone is {zone.Code}, which AutoCAD gives no EPSG code for, so it cannot be "
                + $"shown to be the order's {frame.Crs}. {Remedy}";
        }

        return zoneEpsg == orderEpsg
            ? null
            : $"This drawing's Civil 3D coordinate zone is {ZoneSetting(zone)}, and the order is in {frame.Crs}: placing it here "
                + $"would put the site in the wrong place. {Remedy}";
    }

    private static string? ZoneNotice(AutoCadFileFrame frame, DrawingZone zone)
    {
        if (string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal))
        {
            return null;
        }

        return zone.IsSet
            ? $"This drawing's Civil 3D coordinate zone, {ZoneSetting(zone)}, is the order's coordinate system."
            : $"This drawing has no Civil 3D coordinate zone, so the order is placed at its own coordinates, in {frame.Crs}. "
                + "Set the zone to that coordinate system in Drawing Settings to tie it to the map; the plugin does not set it for you.";
    }

    private static string ZoneSetting(DrawingZone zone) =>
        zone.Epsg is { } epsg
            ? string.Create(CultureInfo.InvariantCulture, $"{zone.Code} (EPSG:{epsg})")
            : zone.Code;

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
        AutoCadPart.Records => "Public-record layers",
        AutoCadPart.Linework => "Site linework",
        _ => "Aerial image",
    };

    private static PlannedPart? LeftOut(AutoCadBlock block, AutoCadPart kind)
    {
        if (!block.LeftOut.TryGetValue(kind, out string? sentence))
        {
            return null;
        }

        (PartReadiness verdict, AutoCadPointer? pointer) = kind switch
        {
            AutoCadPart.Contours => (block.ContoursReadiness, block.Contours),
            AutoCadPart.Surface => (block.SurfaceReadiness, block.Surface),
            AutoCadPart.Aerial => (block.AerialReadiness, block.Aerial),
            AutoCadPart.Records => (block.RecordsReadiness, null),
            _ => (block.LineworkReadiness, block.Linework),
        };

        return Skip(kind, verdict, pointer, sentence);
    }

    private static string WithNotInBundle(string sentence, IReadOnlyList<string> notInBundle) =>
        notInBundle.Count == 0 ? sentence : $"{sentence} Not in this bundle: {string.Join(", ", notInBundle)}.";

    private static PlannedPart? Absent(AutoCadPart kind, PartReadiness verdict, AutoCadPointer? pointer)
        => Absent(kind, verdict, pointer, pointer is not null);

    private static PlannedPart? Absent(AutoCadPart kind, PartReadiness verdict, AutoCadPointer? pointer, bool namesFile)
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

        return namesFile
            ? null
            : Skip(kind, verdict, pointer, $"{label}: not imported. The bundle says it is ready but names no file for it.");
    }

    private static PlannedPart Skip(AutoCadPart kind, PartReadiness verdict, AutoCadPointer? pointer, string sentence) => new()
    {
        Kind = kind,
        Place = false,
        Present = verdict.Present && pointer is not null,
        Ready = verdict.Present,
        VerdictStated = verdict.Stated,
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
        VerdictStated = true,
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

    private static string FrameWord(AutoCadFileFrame frame) =>
        string.Equals(frame.Type, AutoCadFileFrame.Local, StringComparison.Ordinal) ? LocalEnu : AbsoluteProjected;

    private static string? OutOfFrame(AutoCadPointer pointer, AutoCadFileFrame frame, bool hasHeights, bool absoluteHeights = true)
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

        return !absoluteHeights || string.Equals(pointer.VerticalReference, AbsoluteHeights, StringComparison.Ordinal)
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

    private static PlannedPart PlanSurface(AutoCadBlock block, AutoCadFileFrame frame, bool civil3dLoaded)
    {
        PartReadiness verdict = block.SurfaceReadiness;
        AutoCadPointer? pointer = block.Surface;
        string label = Label(AutoCadPart.Surface);

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

        if (OutOfFrame(pointer, frame, hasHeights: true) is { } why)
        {
            return Skip(AutoCadPart.Surface, verdict, pointer, $"{label}: not imported, because {why}.");
        }

        return Placed(
            AutoCadPart.Surface,
            verdict,
            pointer,
            AutoCadLayers.Surface,
            $"{label}: built as a TIN surface from {pointer.Path} on {AutoCadLayers.Surface}, heights in {UnitWords(pointer.VerticalUnits)} ({pointer.VerticalDatum}).");
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

        string format = block.FromThree ? AutoCadThreeView.GeoTiffFormat : AerialPngFormat;
        if (!string.Equals(pointer!.Format, format, StringComparison.Ordinal))
        {
            return Skip(AutoCadPart.Aerial, verdict, pointer, $"{label}: not imported, because its format \"{pointer.Format}\" is one this plugin does not know.");
        }

        if (block.FromThree && OutOfFrame(pointer, frame, hasHeights: false) is { } why)
        {
            return Skip(AutoCadPart.Aerial, verdict, pointer, $"{label}: not imported, because {why}.");
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

    private static PlannedPart PlanRecords(AutoCadBlock block, AutoCadFileFrame frame)
    {
        PartReadiness verdict = block.RecordsReadiness;
        IReadOnlyList<AutoCadRecordLayer> layers = block.Records ?? [];
        string label = Label(AutoCadPart.Records);

        if (Absent(AutoCadPart.Records, verdict, null, layers.Count > 0) is { } absent)
        {
            return absent;
        }

        foreach (AutoCadRecordLayer layer in layers)
        {
            if (OutOfFrame(layer.Pointer, frame, hasHeights: true, absoluteHeights: false) is { } why)
            {
                return Skip(AutoCadPart.Records, verdict, null, $"{label}: not imported, because the {layer.Name} layer: {why}.");
            }
        }

        List<PlannedFile> files = [.. layers.Select(l => new PlannedFile(
            l.Name,
            l.Pointer.Path,
            l.Pointer.Sha256,
            l.Pointer.HorizontalFrame,
            l.Pointer.HorizontalUnits,
            l.Pointer.VerticalUnits,
            AutoCadLayers.Record(l.Name)))];

        return new PlannedPart
        {
            Kind = AutoCadPart.Records,
            Place = true,
            Present = true,
            Ready = true,
            VerdictStated = true,
            Reason = verdict.Reason,
            Sentence = WithNotInBundle(
                $"{label}: placed as polylines, each on its own layer ({string.Join(", ", files.Select(f => f.Layer))}), in {UnitWords(frame.HorizontalUnit)}.",
                block.RecordsLeftOut),
            HorizontalFrame = files[0].HorizontalFrame,
            HorizontalUnits = files[0].HorizontalUnits,
            VerticalUnits = files[0].VerticalUnits,
            Files = files,
            NotInBundle = block.RecordsLeftOut,
        };
    }

    private static PlannedPart PlanLinework(AutoCadBlock block, AutoCadFileFrame frame)
    {
        PartReadiness verdict = block.LineworkReadiness;
        AutoCadPointer? pointer = block.Linework;
        string label = Label(AutoCadPart.Linework);

        if (Absent(AutoCadPart.Linework, verdict, pointer) is { } absent)
        {
            return absent;
        }

        if (OutOfFrame(pointer!, frame, hasHeights: true, absoluteHeights: false) is { } why)
        {
            return Skip(AutoCadPart.Linework, verdict, pointer, $"{label}: not imported, because {why}.");
        }

        return Placed(
            AutoCadPart.Linework,
            verdict,
            pointer!,
            AutoCadLayers.Linework,
            $"{label}: placed as polylines under {AutoCadLayers.Linework}, on the layers the drawing names, in {UnitWords(frame.HorizontalUnit)}.");
    }
}
