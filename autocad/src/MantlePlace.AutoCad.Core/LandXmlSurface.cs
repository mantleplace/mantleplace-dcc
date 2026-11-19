using System.Globalization;
using System.Xml;

namespace MantlePlace.AutoCad;

/// <summary>
/// Reads which surface the block's LandXML holds, so Civil 3D can be asked to build that one by its own
/// name, and, for an MPB 3.x order, holds what the file says of its own frame to the frame the manifest
/// states for it. Pure: text in, a name or the reason there is none out. The surface's points are not
/// read here; Civil 3D reads the file and places them as written.
/// </summary>
/// <remarks>
/// <para>
/// A LandXML file states its linear unit (<c>&lt;Units&gt;&lt;Metric linearUnit="meter"/&gt;</c>, or
/// <c>&lt;Imperial linearUnit="USSurveyFoot"/&gt;</c> or <c>"foot"</c>) and may name its coordinate system
/// (<c>&lt;CoordinateSystem epsgCode="32618"/&gt;</c>). A 3.x bundle's terrain is in the georeferenced
/// family, so both must be the order's: the CRS the manifest's georeferenced frame states
/// (<c>spec/format.md</c> §4), and its units. The file's one linear unit governs its X, Y and its
/// elevations alike, so it must be the frame's horizontal unit and its vertical unit; where the frame's two
/// units differ, one LandXML unit cannot state both, and no surface is built.
/// </para>
/// <para>
/// A file that states another unit or CRS, or a value this plugin does not know, builds no surface and says
/// which; the plugin never scales or reprojects the file to make it agree. Neither is assumed: a file that
/// states no linear unit, or no EPSG code (no <c>&lt;CoordinateSystem&gt;</c>, or one that names its system
/// only by <c>horizontalCoordinateSystemName</c>), is unplaceable, builds no surface, and the sentence says
/// which it does not state (<c>HPS-53</c>).
/// </para>
/// <para>
/// A 2.x plan is read exactly as 1.0 read it: the file's one surface, by name, and nothing else.
/// </para>
/// <para>
/// The drawing has a say too. Civil 3D converts a LandXML file's linear unit to the drawing's Civil 3D
/// drawing units (feet or metres) as it reads the file, so a file in metres read into a drawing in feet is
/// built 3.28 times further from the origin than every other part, which is placed as written.
/// <see cref="DrawingUnitsProblem(AutoCadImportPlan, string)"/> holds the drawing's units to the order's,
/// for a 2.x order and a 3.x one alike; the plugin never sets a drawing's units.
/// </para>
/// </remarks>
public static class LandXmlSurface
{
    private const string EpsgPrefix = "EPSG:";

    /// <summary>Civil 3D's own names for its two drawing units, as its drawing settings read.</summary>
    private const string DrawingUnitsFeet = "Feet";
    private const string DrawingUnitsMetres = "Meters";

    /// <summary>
    /// The name of the file's one surface, or <c>null</c> and the sentence that says why there is none
    /// to build: no surface, more than one (the plugin does not choose), a surface with no name, or text
    /// that is not LandXML. Never throws on the file's content.
    /// </summary>
    public static string? TryReadName(TextReader reader, out string? name) => TryRead(reader, null, out name);

    /// <summary>
    /// As <see cref="TryReadName(TextReader, out string?)"/>, for the surface <paramref name="plan"/>
    /// places. For a 3.x plan the file must state its linear unit and its EPSG code, and both must be the
    /// plan's frame's (the unit both its horizontal and its vertical unit): one unstated, one that is not
    /// the frame's, or one this plugin does not know, is the reason there is no surface to build.
    /// </summary>
    public static string? TryReadName(TextReader reader, AutoCadImportPlan plan, out string? name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return TryRead(reader, plan.FromThree ? plan.Frame : null, out name);
    }

    /// <summary>
    /// Why Civil 3D would not build <paramref name="plan"/>'s surface where the order states it, in a
    /// drawing whose Civil 3D drawing units read <paramref name="drawingUnits"/>, or <c>null</c> when it
    /// would. For a 2.x order and a 3.x one alike.
    /// </summary>
    public static string? DrawingUnitsProblem(AutoCadImportPlan plan, string drawingUnits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DrawingUnitsProblem(plan.Frame, drawingUnits);
    }

    /// <summary>
    /// As <see cref="DrawingUnitsProblem(AutoCadImportPlan, string)"/>, for the order's
    /// <paramref name="frame"/>. Civil 3D converts a LandXML file's linear unit to the drawing's Civil 3D
    /// drawing units as it reads the file: <paramref name="drawingUnits"/> is that setting as Civil 3D
    /// names it, <c>Feet</c> or <c>Meters</c>. A drawing in feet reads a file in either foot as written,
    /// and one in metres a file in metres; any other pairing scales the surface away from the order's other
    /// parts, so it is the reason there is no surface to build. Units this plugin does not know are never
    /// assumed to match.
    /// </summary>
    public static string? DrawingUnitsProblem(AutoCadFileFrame frame, string drawingUnits)
    {
        ArgumentNullException.ThrowIfNull(frame);
        string orderWords = AutoCadImportPlanner.UnitWords(frame.HorizontalUnit) ?? frame.HorizontalUnit;
        (string Name, string Words)? wanted = frame.HorizontalUnit switch
        {
            "m" => (DrawingUnitsMetres, "metres"),
            "ft" or "ftUS" => (DrawingUnitsFeet, "feet"),
            _ => null,
        };
        string? drawingWords = drawingUnits switch
        {
            DrawingUnitsMetres => "metres",
            DrawingUnitsFeet => "feet",
            _ => null,
        };

        if (wanted is { } match && string.Equals(drawingUnits, match.Name, StringComparison.Ordinal))
        {
            return null;
        }

        string set = wanted is { } to
            ? $"; set them to {to.Words} (Drawing Settings, Units and Zone) and import again"
            : string.Empty;
        return drawingWords is null
            ? $"this drawing's Civil 3D drawing units read \"{drawingUnits}\", which this plugin does not know, and the order is in {orderWords}, "
                + "so the drawing is not shown to be in the order's units; Civil 3D converts a LandXML file to the drawing's units as it reads it, "
                + $"and the plugin never changes a drawing's units{set}"
            : $"this drawing's Civil 3D drawing units are {drawingWords} and the order is in {orderWords}; Civil 3D converts a LandXML file to the "
                + "drawing's units as it reads it, which would build the surface away from the order's other parts, and the plugin never changes a "
                + $"drawing's units{set}";
    }

    private static string? TryRead(TextReader reader, AutoCadFileFrame? frame, out string? name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        name = null;
        List<string?> names = [];
        (string Element, string Unit)? units = null;
        string? epsgCode = null;
        bool coordinateSystemRead = false;

        try
        {
            using XmlReader xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            int depth = -1;
            string? topLevel = null;
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                // The root's children: <Units>, <CoordinateSystem>, <Surfaces> among them.
                if (xml.Depth == 1)
                {
                    topLevel = xml.LocalName;
                    if (xml.LocalName == "CoordinateSystem" && !coordinateSystemRead)
                    {
                        coordinateSystemRead = true;
                        epsgCode = xml.GetAttribute("epsgCode");
                    }
                }
                else if (xml.Depth == 2 && topLevel == "Units" && units is null && xml.GetAttribute("linearUnit") is { } linearUnit)
                {
                    units = (xml.LocalName, linearUnit);
                }

                if (xml.LocalName == "Surfaces" && depth < 0)
                {
                    depth = xml.Depth;
                }
                else if (xml.LocalName == "Surface" && depth >= 0 && xml.Depth == depth + 1)
                {
                    names.Add(xml.GetAttribute("name"));
                }
            }
        }
        catch (XmlException ex)
        {
            return $"its file is not LandXML Civil 3D can be asked to read ({ex.Message})";
        }

        if (names.Count == 0)
        {
            return "its file holds no surface";
        }

        if (names.Count > 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"its file holds {names.Count} surfaces, and the plugin does not choose one");
        }

        if (string.IsNullOrWhiteSpace(names[0]))
        {
            return "its file's surface has no name to ask Civil 3D for";
        }

        if (frame is not null && (UnitProblem(units, frame) ?? CrsProblem(coordinateSystemRead, epsgCode, frame)) is { } problem)
        {
            return problem;
        }

        name = names[0];
        return null;
    }

    /// <summary>
    /// Why the file's linear unit is not the frame's, or <c>null</c>. LandXML states one linear unit for the
    /// file, which governs its X, Y and its elevations, so it must be both the frame's horizontal unit and
    /// its vertical unit. An unstated unit is never assumed to match.
    /// </summary>
    private static string? UnitProblem((string Element, string Unit)? units, AutoCadFileFrame frame)
    {
        string horizontalWords = AutoCadImportPlanner.UnitWords(frame.HorizontalUnit) ?? frame.HorizontalUnit;
        string verticalWords = AutoCadImportPlanner.UnitWords(frame.VerticalUnit) ?? frame.VerticalUnit;
        bool oneUnit = string.Equals(frame.HorizontalUnit, frame.VerticalUnit, StringComparison.Ordinal);
        string orderWords = oneUnit
            ? horizontalWords
            : $"coordinates in {horizontalWords} and heights in {verticalWords}";

        if (units is not { } stated)
        {
            return $"its file states no linear unit (no <Units> element with a linearUnit), so it is not shown to be in the order's {orderWords}; "
                + "an unstated unit is never assumed to match";
        }

        string? token = (stated.Element, stated.Unit) switch
        {
            ("Metric", "meter") => "m",
            ("Imperial", "foot") => "ft",
            ("Imperial", "USSurveyFoot") => "ftUS",
            _ => null,
        };

        if (token is null)
        {
            return $"its file states its linear unit as <{stated.Element} linearUnit=\"{stated.Unit}\">, which this plugin does not know, "
                + $"so it is not shown to be in the order's {orderWords}";
        }

        string fileWords = AutoCadImportPlanner.UnitWords(token) ?? token;
        if (!oneUnit)
        {
            return $"its file states one linear unit, {fileWords} (<{stated.Element} linearUnit=\"{stated.Unit}\">), for its coordinates and heights "
                + $"alike, and the order's frame states {orderWords}; one LandXML unit cannot state both, and the plugin does not convert, "
                + "so no surface was built";
        }

        return string.Equals(token, frame.HorizontalUnit, StringComparison.Ordinal)
            ? null
            : $"its file states its coordinates and heights in {fileWords} (<{stated.Element} linearUnit=\"{stated.Unit}\">), and the "
                + $"order's frame states {orderWords}; the plugin does not convert, so no surface was built in the wrong place";
    }

    /// <summary>
    /// Why the file's coordinate system is not the frame's, or <c>null</c>. An unstated CRS (no
    /// <c>&lt;CoordinateSystem&gt;</c>, or one with no <c>epsgCode</c>) is never assumed to match.
    /// </summary>
    private static string? CrsProblem(bool coordinateSystemRead, string? epsgCode, AutoCadFileFrame frame)
    {
        if (string.IsNullOrWhiteSpace(epsgCode))
        {
            string where = coordinateSystemRead ? "its <CoordinateSystem> has no epsgCode" : "it has no <CoordinateSystem>";
            return $"its file states no EPSG code ({where}), so it is not shown to be in the order's {frame.Crs}; "
                + "an unstated coordinate system is never assumed to match";
        }

        string code = epsgCode.Trim();
        string digits = code.StartsWith(EpsgPrefix, StringComparison.OrdinalIgnoreCase) ? code[EpsgPrefix.Length..] : code;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int fileEpsg))
        {
            return $"its file states the coordinate system code \"{code}\", which is not one EPSG code this plugin reads, so it is not "
                + $"shown to be the order's {frame.Crs}";
        }

        string fileCrs = string.Create(CultureInfo.InvariantCulture, $"{EpsgPrefix}{fileEpsg}");
        return string.Equals(fileCrs, frame.Crs, StringComparison.Ordinal)
            ? null
            : $"its file states the coordinate system {fileCrs}, and the order is in {frame.Crs}; the plugin does not reproject, so no "
                + "surface was built in the wrong place";
    }
}
