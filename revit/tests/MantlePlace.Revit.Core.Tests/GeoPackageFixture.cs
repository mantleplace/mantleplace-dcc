using System.Globalization;
using System.Text;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Builds the bytes of a synthetic vector carrier: GeoPackage geometry blobs (the <c>GP</c> header and
/// WKB) and, for the Windows suite, the SQL that makes them a table. No binary file is checked in; the
/// carrier a case reads is written here, in the test, and thrown away.
/// </summary>
internal static class GeoPackageFixture
{
    /// <summary>A GeoPackage geometry blob: the <c>GP</c> header, an envelope of the given kind, then the WKB.</summary>
    /// <param name="envelope">0 none, 1 XY, 2 XYZ, 3 XYM, 4 XYZM — the header's envelope code.</param>
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

    /// <summary>A 3-D line, ISO WKB (type 1002), from x, y, z triples.</summary>
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

    /// <summary>A 4-D line (X, Y, Z, M), ISO WKB (type 3002), from x, y, z, m quadruples.</summary>
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

    /// <summary>A multi-line, planar, each part from x, y pairs.</summary>
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

    /// <summary>A polygon, each ring from x, y pairs and exterior first; the caller closes its rings.</summary>
    internal static byte[] Polygon(params double[][] ringsXy)
        => Wkb(writer => WritePolygon(writer, ringsXy));

    /// <summary>A multi-polygon: each polygon its rings, each ring x, y pairs.</summary>
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

    /// <summary>A point, which is not linework and must be passed over.</summary>
    internal static byte[] Point(double x, double y)
        => Wkb(writer =>
        {
            writer.Write((byte)1);
            writer.Write(1u);
            writer.Write(x);
            writer.Write(y);
        });

    /// <summary>A closed axis-aligned square ring, counter-clockwise, as x, y pairs.</summary>
    internal static double[] Square(double west, double south, double side)
        =>
        [
            west, south,
            west + side, south,
            west + side, south + side,
            west, south + side,
            west, south,
        ];

    /// <summary>SQLite's blob literal for <paramref name="bytes"/>: <c>X'4750…'</c>.</summary>
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
