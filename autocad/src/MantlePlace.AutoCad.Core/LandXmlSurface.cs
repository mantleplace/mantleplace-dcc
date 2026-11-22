using System.Globalization;
using System.Xml;

namespace MantlePlace.AutoCad;

public static class LandXmlSurface
{
    private const string EpsgPrefix = "EPSG:";

    private const string DrawingUnitsFeet = "Feet";
    private const string DrawingUnitsMetres = "Meters";

    public static string? TryReadName(TextReader reader, out string? name) => TryRead(reader, null, out name);

    public static string? TryReadName(TextReader reader, AutoCadImportPlan plan, out string? name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return TryRead(reader, plan.FromThree ? plan.Frame : null, out name);
    }

    public static string? DrawingUnitsProblem(AutoCadImportPlan plan, string drawingUnits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DrawingUnitsProblem(plan.Frame, drawingUnits);
    }

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
