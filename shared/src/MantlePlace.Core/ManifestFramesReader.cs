using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MantlePlace.Core;

/// <summary>
/// Reads the <c>frames</c> member of a 3.x manifest (<c>spec/format.md</c> §9.4): the georeferenced
/// frame and the local site frame, each stated once. Pure: JSON in, facts out.
/// </summary>
/// <remarks>
/// <para>
/// Every value is read from where the manifest states it, and none stands in for one it does not:
/// the local frame's unit is read like any other. A value the reader does not understand fails the
/// whole reading, naming it (<c>spec/format.md</c> §4), because a host would otherwise place
/// geometry in a frame it could not name. This reader checks what a reader needs to apply the
/// frames verbatim; it takes no CRS apart and derives no placement.
/// </para>
/// </remarks>
internal static partial class ManifestFramesReader
{
    private const string Frames = "frames";

    /// <summary>The relative tolerance within which a stated factor is one this reader knows.</summary>
    private const double FactorTolerance = 1e-9;

    private const double InternationalFootToMetres = 0.3048;

    private const double UsSurveyFootToMetres = 1200.0 / 3937.0;

    /// <summary>
    /// Reads <c>frames</c> from <paramref name="root"/>. <c>null</c> with <paramref name="error"/> set
    /// when the manifest states them in a way this reader cannot apply.
    /// </summary>
    internal static BundleFrames? Read(JsonElement root, out string? error)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(Frames, out JsonElement frames))
            {
                throw new FramesRefusedException(
                    "Manifest.json states no frames, which a reader needs to place any file (spec/format.md §9.4).");
            }

            Object(frames, Frames);
            JsonElement order = Member(frames, "georeferenced", Frames);
            JsonElement local = Member(frames, "local", Frames);
            error = null;
            return new BundleFrames(ReadGeoreferenced(order), ReadLocal(local));
        }
        catch (FramesRefusedException refusal)
        {
            error = refusal.Message;
            return null;
        }
    }

    private static GeoreferencedFrame ReadGeoreferenced(JsonElement order)
    {
        const string where = "frames.georeferenced";
        Object(order, where);
        return new GeoreferencedFrame(
            ReadCrs(Member(order, "horizontal_crs", where), $"{where}.horizontal_crs"),
            ReadCrs(Member(order, "vertical_crs", where), $"{where}.vertical_crs"),
            ReadGeoid(order, where),
            ReadUnit(Member(order, "horizontal_unit", where), $"{where}.horizontal_unit"),
            ReadUnit(Member(order, "vertical_unit", where), $"{where}.vertical_unit"),
            ReadAxisOrder(order, where),
            ReadNumber(order, "grid_rotation_deg", where));
    }

    private static LocalSiteFrame ReadLocal(JsonElement local)
    {
        const string where = "frames.local";
        Object(local, where);
        SiteOrigin origin = ReadOrigin(Member(local, "origin", where), $"{where}.origin");
        FrameCrs vertical = ReadCrs(Member(local, "vertical_crs", where), $"{where}.vertical_crs");
        FrameUnit unit = ReadUnit(Member(local, "unit", where), $"{where}.unit");
        ReadAxes(Member(local, "axes", where), $"{where}.axes");

        // The local frame is in metres on every order (spec/format.md §9.4). It is read, and a unit
        // that is anything else is refused rather than converted: no host re-derives a placement.
        if (unit.Unit != LinearUnit.Metre)
        {
            throw new FramesRefusedException(string.Format(
                CultureInfo.InvariantCulture,
                "Manifest.json states the local site frame's unit as {0} ({1} metres), but that frame is in metres on every order.",
                unit.Name,
                Factor(unit.ToMetres)));
        }

        return new LocalSiteFrame(
            origin,
            vertical,
            ReadGeoid(local, where),
            unit,
            ReadNumber(local, "grid_rotation_deg", where));
    }

    private static SiteOrigin ReadOrigin(JsonElement origin, string where)
    {
        Object(origin, where);
        JsonElement projected = Member(origin, "projected", where);
        string at = $"{where}.projected";
        Object(projected, at);
        return new SiteOrigin(
            ReadNumber(origin, "longitude", where),
            ReadNumber(origin, "latitude", where),
            ReadNumber(origin, "ellipsoidal_height_m", where),
            ReadNumber(origin, "height_m", where),
            new ProjectedOrigin(
                ReadCrs(Member(projected, "crs", at), $"{at}.crs"),
                ReadNumber(projected, "x", at),
                ReadNumber(projected, "y", at),
                ReadUnit(Member(projected, "unit", at), $"{at}.unit")));
    }

    private static void ReadAxes(JsonElement axes, string where)
    {
        Object(axes, where);
        foreach ((string axis, string direction) in new[] { ("x", "east"), ("y", "north"), ("z", "up") })
        {
            string stated = axes.TryGetProperty(axis, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : "(absent)";
            if (!string.Equals(stated, direction, StringComparison.Ordinal))
            {
                throw new FramesRefusedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Manifest.json's {0} states the {1} axis as {2}, which is not {3}: the local site frame runs east, north, up.",
                    where,
                    axis,
                    stated,
                    direction));
            }
        }
    }

    private static AxisOrder ReadAxisOrder(JsonElement order, string where)
    {
        string stated = order.TryGetProperty("axis_order", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : "(absent)";
        return stated switch
        {
            "east-north" => AxisOrder.EastNorth,
            "north-east" => AxisOrder.NorthEast,
            _ => throw new FramesRefusedException(string.Format(
                CultureInfo.InvariantCulture,
                "Manifest.json's {0}.axis_order states {1}, which is not east-north or north-east.",
                where,
                stated)),
        };
    }

    private static FrameUnit ReadUnit(JsonElement unit, string where)
    {
        Object(unit, where);
        string name = unit.TryGetProperty("name", out JsonElement named) && named.ValueKind == JsonValueKind.String
            ? named.GetString() ?? string.Empty
            : string.Empty;
        if (name.Length == 0)
        {
            throw new FramesRefusedException($"Manifest.json's {where} states no name for its unit.");
        }

        if (!unit.TryGetProperty("to_metres", out JsonElement factor) || factor.ValueKind != JsonValueKind.Number)
        {
            throw new FramesRefusedException(
                $"Manifest.json's {where} ({name}) states no to_metres factor, so its unit is not known.");
        }

        double toMetres = factor.TryGetDouble(out double parsed) ? parsed : double.NaN;
        LinearUnit known = Classify(toMetres);
        if (known == LinearUnit.Unspecified)
        {
            throw new FramesRefusedException(string.Format(
                CultureInfo.InvariantCulture,
                "Manifest.json's {0} states the unit {1} ({2} metres), which this plugin does not understand: it reads the metre, the international foot and the US survey foot.",
                where,
                name,
                factor.GetRawText()));
        }

        return new FrameUnit(name, toMetres, known);
    }

    /// <summary>The unit a stated factor to metres is, or <see cref="LinearUnit.Unspecified"/> for one this reader does not know.</summary>
    private static LinearUnit Classify(double toMetres)
    {
        if (!double.IsFinite(toMetres) || toMetres <= 0)
        {
            return LinearUnit.Unspecified;
        }

        return Near(toMetres, 1.0) ? LinearUnit.Metre
            : Near(toMetres, InternationalFootToMetres) ? LinearUnit.InternationalFoot
            : Near(toMetres, UsSurveyFootToMetres) ? LinearUnit.UsSurveyFoot
            : LinearUnit.Unspecified;
    }

    private static bool Near(double stated, double known) => Math.Abs(stated - known) <= known * FactorTolerance;

    private static FrameCrs ReadCrs(JsonElement crs, string where)
    {
        Object(crs, where);
        string? code = null;
        string? projJson = null;
        string? wkt2 = null;

        if (crs.TryGetProperty("code", out JsonElement codeValue))
        {
            code = codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
            if (code is null || !CodePattern().IsMatch(code))
            {
                throw new FramesRefusedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Manifest.json's {0} names the code {1}, which is not an authority code such as EPSG:32618.",
                    where,
                    codeValue.GetRawText()));
            }
        }

        if (crs.TryGetProperty("projjson", out JsonElement projValue))
        {
            if (projValue.ValueKind != JsonValueKind.Object)
            {
                throw new FramesRefusedException($"Manifest.json's {where}.projjson is not a PROJJSON object.");
            }

            projJson = projValue.GetRawText();
        }

        if (crs.TryGetProperty("wkt2", out JsonElement wktValue))
        {
            wkt2 = wktValue.ValueKind == JsonValueKind.String ? wktValue.GetString() : null;
            if (string.IsNullOrEmpty(wkt2))
            {
                throw new FramesRefusedException($"Manifest.json's {where}.wkt2 is not a WKT2 string.");
            }
        }

        return code is null && projJson is null && wkt2 is null
            ? throw new FramesRefusedException($"Manifest.json's {where} names no code, PROJJSON object or WKT2 string, so it names no CRS.")
            : new FrameCrs(code, projJson, wkt2);
    }

    private static string? ReadGeoid(JsonElement frame, string where)
    {
        if (!frame.TryGetProperty("geoid_model", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new FramesRefusedException($"Manifest.json's {where}.geoid_model is not a name or null.");
    }

    private static double ReadNumber(JsonElement parent, string name, string where)
    {
        JsonElement value = Member(parent, name, where);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : throw new FramesRefusedException(string.Format(
                CultureInfo.InvariantCulture,
                "Manifest.json's {0}.{1} states {2}, which is not a number.",
                where,
                name,
                value.GetRawText()));
    }

    private static JsonElement Member(JsonElement parent, string name, string where) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new FramesRefusedException($"Manifest.json states no {where}.{name}, which a reader of its frames needs.");

    private static void Object(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FramesRefusedException($"Manifest.json's {where} is not an object.");
        }
    }

    private static string Factor(double toMetres) => toMetres.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>An authority code, the way the served schema words it: <c>EPSG:32618</c>.</summary>
    [GeneratedRegex("^[A-Z]+:[0-9A-Za-z.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    private sealed class FramesRefusedException(string message) : Exception(message);
}
