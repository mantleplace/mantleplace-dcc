using System.Buffers.Binary;

namespace MantlePlace.Core;

public readonly record struct GeoPackagePosition(double X, double Y, double? Z);

public sealed record GeoPackagePart(IReadOnlyList<GeoPackagePosition> Positions, bool Closed, bool IsHole, int Polygon);

public static class GeoPackageGeometry
{
    private const int HeaderBytes = 8;

    public static IReadOnlyList<GeoPackagePart> Decode(byte[] blob)
    {
        List<GeoPackagePart> parts = [];
        if (blob is null || blob.Length < HeaderBytes || blob[0] != (byte)'G' || blob[1] != (byte)'P')
        {
            return parts;
        }

        byte flags = blob[3];
        if ((flags & 0b0001_0000) != 0)
        {
            return parts;
        }

        int envelopeDoubles = ((flags >> 1) & 0b111) switch
        {
            0 => 0,
            1 => 4,
            2 or 3 => 6,
            4 => 8,
            _ => -1,
        };
        if (envelopeDoubles < 0)
        {
            return parts;
        }

        int offset = HeaderBytes + (envelopeDoubles * 8);
        int polygons = 0;
        try
        {
            ReadGeometry(blob, ref offset, parts, ref polygons);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            parts.Clear();
        }

        return parts;
    }

    public static IReadOnlyList<GeoPackagePart> Decode(byte[] blob, ref int polygonsSoFar)
    {
        IReadOnlyList<GeoPackagePart> parts = Decode(blob);
        int highest = 0;
        List<GeoPackagePart> renumbered = new(parts.Count);
        foreach (GeoPackagePart part in parts)
        {
            highest = Math.Max(highest, part.Polygon);
            renumbered.Add(part.Polygon == 0 ? part : part with { Polygon = part.Polygon + polygonsSoFar });
        }

        polygonsSoFar += highest;
        return renumbered;
    }

    private static void ReadGeometry(byte[] wkb, ref int offset, List<GeoPackagePart> parts, ref int polygons)
    {
        bool little = wkb[offset] == 1;
        offset++;
        uint raw = ReadUInt32(wkb, ref offset, little);
        bool hasZ = (raw & 0x8000_0000u) != 0;
        bool hasM = (raw & 0x4000_0000u) != 0;
        uint type = raw & 0x0FFF_FFFFu;
        switch (type / 1000)
        {
            case 1:
                hasZ = true;
                break;
            case 2:
                hasM = true;
                break;
            case 3:
                hasZ = true;
                hasM = true;
                break;
            default:
                break;
        }

        type %= 1000;
        int dimensions = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        switch (type)
        {
            case 2:
                parts.Add(new GeoPackagePart(ReadPositions(wkb, ref offset, little, dimensions, hasZ), Closed: false, IsHole: false, Polygon: 0));
                break;

            case 3:
                polygons++;
                uint rings = ReadUInt32(wkb, ref offset, little);
                for (uint ring = 0; ring < rings; ring++)
                {
                    parts.Add(new GeoPackagePart(ReadPositions(wkb, ref offset, little, dimensions, hasZ), Closed: true, IsHole: ring > 0, Polygon: polygons));
                }

                break;

            case 5:
            case 6:
            case 7:
                uint members = ReadUInt32(wkb, ref offset, little);
                for (uint member = 0; member < members; member++)
                {
                    ReadGeometry(wkb, ref offset, parts, ref polygons);
                }

                break;

            case 1:
                offset += dimensions * 8;
                break;

            case 4:
                uint points = ReadUInt32(wkb, ref offset, little);
                for (uint point = 0; point < points; point++)
                {
                    offset += 5;
                    offset += dimensions * 8;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(wkb), type, "a WKB type this decoder does not follow");
        }
    }

    private static List<GeoPackagePosition> ReadPositions(byte[] wkb, ref int offset, bool little, int dimensions, bool hasZ)
    {
        uint count = ReadUInt32(wkb, ref offset, little);

        if ((ulong)count * (ulong)(dimensions * 8) > (ulong)(wkb.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(wkb), count, "more positions than the blob holds");
        }

        List<GeoPackagePosition> positions = new((int)count);
        int skipped = (dimensions - 2 - (hasZ ? 1 : 0)) * 8;
        for (uint index = 0; index < count; index++)
        {
            double x = ReadDouble(wkb, ref offset, little);
            double y = ReadDouble(wkb, ref offset, little);
            double? z = hasZ ? ReadDouble(wkb, ref offset, little) : null;
            offset += skipped;
            positions.Add(new GeoPackagePosition(x, y, z));
        }

        return positions;
    }

    private static uint ReadUInt32(byte[] bytes, ref int offset, bool little)
    {
        ReadOnlySpan<byte> span = bytes.AsSpan(offset, 4);
        offset += 4;
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
    }

    private static double ReadDouble(byte[] bytes, ref int offset, bool little)
    {
        ReadOnlySpan<byte> span = bytes.AsSpan(offset, 8);
        offset += 8;
        return little ? BinaryPrimitives.ReadDoubleLittleEndian(span) : BinaryPrimitives.ReadDoubleBigEndian(span);
    }
}
