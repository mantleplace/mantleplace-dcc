using System.Globalization;
using System.Text;

namespace MantlePlace.Core.Tests;

internal static class GeoPackageFixture
{
    internal static byte[] Blob(int srsId, byte[] wkb, int envelope = 1, bool empty = false)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)'G');
        writer.Write((byte)'P');
        writer.Write((byte)0);
        writer.Write((byte)(1 | (envelope << 1) | (empty ? 0b0001_0000 : 0)));
        writer.Write(srsId);
        int doubles = envelope switch { 0 => 0, 1 => 4, 4 => 8, _ => 6 };
        for (int index = 0; index < doubles; index++)
        {
            writer.Write(0.0);
        }

        writer.Write(wkb);
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] LineStringZ(params double[] xyz)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(1002u);
            writer.Write(xyz.Length / 3);
            foreach (double value in xyz)
            {
                writer.Write(value);
            }
        });

    internal static byte[] LineStringZm(params double[] xyzm)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(3002u);
            writer.Write(xyzm.Length / 4);
            foreach (double value in xyzm)
            {
                writer.Write(value);
            }
        });

    internal static byte[] MultiLineString(params double[][] parts)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(5u);
            writer.Write(parts.Length);
            foreach (double[] xy in parts)
            {
                writer.Write((byte)1);
                writer.Write(2u);
                writer.Write(xy.Length / 2);
                foreach (double value in xy)
                {
                    writer.Write(value);
                }
            }
        });

    internal static byte[] Polygon(params double[][] ringsXy)
        => Wkb(writer => WritePolygon(writer, ringsXy));

    internal static byte[] MultiPolygon(params double[][][] polygons)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(6u);
            writer.Write(polygons.Length);
            foreach (double[][] rings in polygons)
            {
                WritePolygon(writer, rings);
            }
        });

    internal static byte[] Point(double x, double y)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(1u);
            writer.Write(x);
            writer.Write(y);
        });

    internal static double[] Square(double west, double south, double side)
        =>
        [
            west, south,
            west + side, south,
            west + side, south + side,
            west, south + side,
            west, south,
        ];

    internal static string Hex(byte[] bytes)
    {
        StringBuilder text = new("X'");
        foreach (byte value in bytes)
        {
            text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.Append('\'').ToString();
    }

    private static void WritePolygon(BinaryWriter writer, double[][] ringsXy)
    {
        writer.Write((byte)1);
        writer.Write(3u);
        writer.Write(ringsXy.Length);
        foreach (double[] xy in ringsXy)
        {
            writer.Write(xy.Length / 2);
            foreach (double value in xy)
            {
                writer.Write(value);
            }
        }
    }

    private static byte[] Wkb(Action<BinaryWriter> body)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        body(writer);
        writer.Flush();
        return stream.ToArray();
    }
}
