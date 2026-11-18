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
/// family, so both must be the order's: the X/Y unit and the CRS the manifest's georeferenced frame states
/// (<c>spec/format.md</c> §4). A file that states another, or a value this plugin does not know, builds no
/// surface and says which; the plugin never scales or reprojects the file to make it agree. A file that
/// states neither is in the frame the manifest states for it, which is the one applied.
/// </para>
/// <para>
/// A 2.x plan is read exactly as 1.0 read it: the file's one surface, by name, and nothing else.
/// </para>
/// </remarks>
public static class LandXmlSurface
{
    private const string EpsgPrefix = "EPSG:";

    /// <summary>
    /// The name of the file's one surface, or <c>null</c> and the sentence that says why there is none
    /// to build: no surface, more than one (the plugin does not choose), a surface with no name, or text
    /// that is not LandXML. Never throws on the file's content.
    /// </summary>
    public static string? TryReadName(TextReader reader, out string? name) => TryRead(reader, null, out name);

    /// <summary>
    /// As <see cref="TryReadName(TextReader, out string?)"/>, for the surface <paramref name="plan"/>
    /// places. For a 3.x plan the file's own linear unit and coordinate system, where it states them, must
    /// be the plan's frame's: one that is not, or one this plugin does not know, is the reason there is no
    /// surface to build.
    /// </summary>
    public static string? TryReadName(TextReader reader, AutoCadImportPlan plan, out string? name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return TryRead(reader, plan.FromThree ? plan.Frame : null, out name);
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

        if (frame is not null && (UnitProblem(units, frame) ?? CrsProblem(epsgCode, frame)) is { } problem)
        {
            return problem;
        }

        name = names[0];
        return null;
    }

    /// <summary>
    /// Why the file's stated linear unit is not the frame's X/Y unit, or <c>null</c>. LandXML states one
    /// linear unit for the file, which is its coordinates' (a 3.x order's heights are in the unit the
    /// manifest states for them, which the plan has already shown it knows).
    /// </summary>
    private static string? UnitProblem((string Element, string Unit)? units, AutoCadFileFrame frame)
    {
        if (units is not { } stated)
        {
            return null;
        }

        string orderWords = AutoCadImportPlanner.UnitWords(frame.HorizontalUnit) ?? frame.HorizontalUnit;
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

        return string.Equals(token, frame.HorizontalUnit, StringComparison.Ordinal)
            ? null
            : $"its file states its coordinates in {AutoCadImportPlanner.UnitWords(token)} (<{stated.Element} linearUnit=\"{stated.Unit}\">), and the "
                + $"order's frame states {orderWords}; the plugin does not convert, so no surface was built in the wrong place";
    }

    /// <summary>Why the file's stated coordinate system is not the frame's, or <c>null</c> when it states none.</summary>
    private static string? CrsProblem(string? epsgCode, AutoCadFileFrame frame)
    {
        if (string.IsNullOrWhiteSpace(epsgCode))
        {
            return null;
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
