using System.Buffers.Binary;

namespace MantlePlace.Revit.CarrierSpike;

public static class GeoPackageGeometry
{
    public static IReadOnlyList<CarrierPart> Decode(byte[] blob)
    {
        List<CarrierPart> parts = [];
        if (blob is null || blob.Length < 8 || blob[0] != (byte)'G' || blob[1] != (byte)'P')
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

        int offset = 8 + (envelopeDoubles * 8);
        int polygons = 0;
        try
        {
            ReadGeometry(blob, ref offset, parts, ref polygons);
        }
        catch (ArgumentOutOfRangeException)
        {
            parts.Clear();
        }

        return parts;
    }

    public static IReadOnlyList<CarrierPart> Decode(byte[] blob, ref int polygonsSoFar)
    {
        IReadOnlyList<CarrierPart> parts = Decode(blob);
        int highest = 0;
        List<CarrierPart> renumbered = new(parts.Count);
        foreach (CarrierPart part in parts)
        {
            highest = Math.Max(highest, part.Polygon);
            renumbered.Add(part.Polygon == 0 ? part : part with { Polygon = part.Polygon + polygonsSoFar });
        }

        polygonsSoFar += highest;
        return renumbered;
    }

    private static void ReadGeometry(byte[] wkb, ref int offset, List<CarrierPart> parts, ref int polygons)
    {
        bool little = wkb[offset] == 1;
        offset++;
        uint raw = ReadUInt32(wkb, ref offset, little);
        bool hasZ = (raw & 0x8000_0000u) != 0;
        bool hasM = (raw & 0x4000_0000u) != 0;
        uint type = raw & 0x0FFF_FFFFu;
        switch (type / 1000)
        {
            case 1: hasZ = true; break;
            case 2: hasM = true; break;
            case 3: hasZ = true; hasM = true; break;
            default: break;
        }

        type %= 1000;
        int dims = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        switch (type)
        {
            case 2:
                parts.Add(new CarrierPart(ReadPositions(wkb, ref offset, little, dims, hasZ), Closed: false, IsHole: false, Polygon: 0));
                break;

            case 3:
                polygons++;
                uint rings = ReadUInt32(wkb, ref offset, little);
                for (uint ring = 0; ring < rings; ring++)
                {
                    parts.Add(new CarrierPart(ReadPositions(wkb, ref offset, little, dims, hasZ), Closed: true, IsHole: ring > 0, Polygon: polygons));
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
                offset += dims * 8;
                break;

            case 4:
                uint points = ReadUInt32(wkb, ref offset, little);
                for (uint point = 0; point < points; point++)
                {
                    offset += 5;
                    offset += dims * 8;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(wkb), type, "a WKB type this decoder does not follow");
        }
    }

    private static List<CarrierPosition> ReadPositions(byte[] wkb, ref int offset, bool little, int dims, bool hasZ)
    {
        uint count = ReadUInt32(wkb, ref offset, little);
        List<CarrierPosition> positions = new((int)Math.Min(count, 1_000_000));
        for (uint i = 0; i < count; i++)
        {
            double x = ReadDouble(wkb, ref offset, little);
            double y = ReadDouble(wkb, ref offset, little);
            double? z = hasZ ? ReadDouble(wkb, ref offset, little) : null;
            offset += (dims - 2 - (hasZ ? 1 : 0)) * 8;
            positions.Add(new CarrierPosition(x, y, z));
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
