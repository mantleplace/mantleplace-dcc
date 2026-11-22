using System.Globalization;
using MantlePlace.Core;

namespace MantlePlace.Revit.CarrierSpike;

public static class DxfLayerReader
{
    public const string Application = "MANTLEPLACE";

    public static string? TryParse(
        string dxfText,
        string layerName,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string label,
        string? levelField,
        out IReadOnlyList<SiteFeature> features)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<SiteFeature> parsed = [];
        features = parsed;

        List<(int Code, string Value)> pairs = Pairs(dxfText ?? string.Empty);
        int start = -1;
        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            if (pairs[i] is (0, "SECTION") && pairs[i + 1] is (2, "ENTITIES"))
            {
                start = i + 2;
                break;
            }
        }

        if (start < 0)
        {
            return $"The {label} carrier has no ENTITIES section, so it is not a drawing this host can read.";
        }

        Entity? polyline = null;
        Entity? current = null;
        for (int i = start; i < pairs.Count; i++)
        {
            (int code, string value) = pairs[i];
            if (code == 0)
            {
                if (current is not null)
                {
                    if (current.Kind == "VERTEX" && polyline is not null)
                    {
                        polyline.Positions.Add(current.Point());
                    }
                    else if (current.Kind == "POLYLINE")
                    {
                        polyline = current;
                    }
                    else if (current.Kind == "LWPOLYLINE")
                    {
                        Emit(current);
                    }
                }

                if (value == "SEQEND" && polyline is not null)
                {
                    Emit(polyline);
                    polyline = null;
                }

                if (value == "ENDSEC")
                {
                    break;
                }

                current = new Entity(value);
                continue;
            }

            current?.Take(code, value);
        }

        return null;

        void Emit(Entity entity)
        {
            if (!entity.Layer.Equals(layerName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool closed = (entity.Flags & 1) != 0;
            Dictionary<string, string> attributes = entity.Attributes;
            int polygon = closed && attributes.TryGetValue("polygon", out string? n)
                && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) ? ordinal : 0;
            bool hole = closed && attributes.TryGetValue("ring", out string? role) && role == "hole";
            CarrierFeatureBuilder.Append(parsed, new CarrierPart(entity.Positions, closed, hole, polygon), attributes, frame, layer, accept, levelField);
        }
    }

    private static List<(int Code, string Value)> Pairs(string text)
    {
        List<(int, string)> pairs = [];
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } codeLine && reader.ReadLine() is { } valueLine)
        {
            if (int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                pairs.Add((code, valueLine.Trim()));
            }
        }

        return pairs;
    }

    private sealed class Entity(string kind)
    {
        private double? _x;
        private double? _y;
        private double? _z;
        private double? _elevation;
        private bool _inXdata;

        public string Kind { get; } = kind;

        public string Layer { get; private set; } = string.Empty;

        public int Flags { get; private set; }

        public List<CarrierPosition> Positions { get; } = [];

        public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

        public CarrierPosition Point() => new(_x ?? 0, _y ?? 0, _z);

        public void Take(int code, string value)
        {
            if (code == 1001)
            {
                _inXdata = value == Application;
                return;
            }

            if (code >= 1000)
            {
                if (_inXdata && code == 1000 && value.IndexOf('=', StringComparison.Ordinal) is > 0 and int at)
                {
                    Attributes[value[..at]] = value[(at + 1)..];
                }

                return;
            }

            double Number() => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (code)
            {
                case 8:
                    Layer = value;
                    break;
                case 70:
                    Flags = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case 38:
                    _elevation = Number();
                    break;
                case 10:
                    _x = Number();
                    break;
                case 20:
                    _y = Number();
                    if (Kind == "LWPOLYLINE")
                    {
                        Positions.Add(new CarrierPosition(_x ?? 0, _y.Value, _elevation));
                    }

                    break;
                case 30:
                    _z = Number();
                    break;
                default:
                    break;
            }
        }
    }
}
