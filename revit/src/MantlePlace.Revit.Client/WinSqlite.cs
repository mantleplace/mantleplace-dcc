using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MantlePlace.Revit.Client;

/// <summary>
/// The SQLite Windows ships as <c>winsqlite3.dll</c> (System32, Windows 10 1607 and later), reached by
/// P/Invoke: no NuGet package, no native binary shipped beside the add-in, and nothing for another
/// add-in's SQLite to collide with inside Revit's process. The few calls a read-only GeoPackage read
/// needs, and no more.
/// </summary>
/// <remarks>
/// <c>revit/Directory.Build.props</c> forbids a package, and <c>Microsoft.Data.Sqlite</c> would bring three
/// and a native <c>e_sqlite3.dll</c> another add-in's copy can shadow. The cost of this route is that the
/// read runs on Windows only, which is where Revit runs; the decoder and everything that places a
/// feature is in <c>MantlePlace.Revit.Core</c> and runs everywhere (<c>revit/docs/vector-carrier.md</c>).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WinSqlite
{
    private const string Library = "winsqlite3.dll";

    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadOnly = 0x0000_0001;

    internal const int Float = 2;
    internal const int Blob = 4;
    internal const int Null = 5;

    /// <summary>Runs every row of <paramref name="sql"/> against a read-only open of <paramref name="path"/>.</summary>
    /// <param name="onRow">Called once per row with that row's reader, which is valid only inside the call.</param>
    /// <exception cref="IOException">SQLite could not open the file, or the file is not a database.</exception>
    internal static void Query(string path, string sql, Action<RowReader> onRow)
    {
        ArgumentNullException.ThrowIfNull(onRow);
        IntPtr db = Open(path);
        try
        {
            byte[] text = Utf8(sql);
            Check(db, NativeMethods.sqlite3_prepare_v2(db, text, text.Length, out IntPtr statement, IntPtr.Zero));
            try
            {
                RowReader reader = new(statement);
                while (true)
                {
                    int step = NativeMethods.sqlite3_step(statement);
                    if (step == Done)
                    {
                        break;
                    }

                    if (step != Row)
                    {
                        Check(db, step);
                    }

                    onRow(reader);
                }
            }
            finally
            {
                _ = NativeMethods.sqlite3_finalize(statement);
            }
        }
        finally
        {
            _ = NativeMethods.sqlite3_close_v2(db);
        }
    }

    private static IntPtr Open(string path)
    {
        int code = NativeMethods.sqlite3_open_v2(Utf8(path), out IntPtr db, OpenReadOnly, IntPtr.Zero);
        if (code != Ok)
        {
            string message = db == IntPtr.Zero ? $"code {code.ToString(CultureInfo.InvariantCulture)}" : Message(db);
            _ = NativeMethods.sqlite3_close_v2(db);
            throw new IOException($"SQLite could not open the file: {message}");
        }

        return db;
    }

    private static void Check(IntPtr db, int code)
    {
        if (code != Ok)
        {
            throw new IOException($"SQLite: {Message(db)} (code {code.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    private static string Message(IntPtr db) => Marshal.PtrToStringUTF8(NativeMethods.sqlite3_errmsg(db)) ?? "unknown error";

    private static byte[] Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Resize(ref bytes, bytes.Length + 1); // NUL-terminated
        return bytes;
    }

    /// <summary>The current row of a statement.</summary>
    internal sealed class RowReader
    {
        private readonly IntPtr _statement;

        internal RowReader(IntPtr statement) => _statement = statement;

        internal int ColumnCount => NativeMethods.sqlite3_column_count(_statement);

        internal string ColumnName(int column)
            => Marshal.PtrToStringUTF8(NativeMethods.sqlite3_column_name(_statement, column)) ?? string.Empty;

        internal int Type(int column) => NativeMethods.sqlite3_column_type(_statement, column);

        internal double Double(int column) => NativeMethods.sqlite3_column_double(_statement, column);

        internal long Int64(int column) => NativeMethods.sqlite3_column_int64(_statement, column);

        internal string String(int column)
        {
            IntPtr text = NativeMethods.sqlite3_column_text(_statement, column);
            int length = NativeMethods.sqlite3_column_bytes(_statement, column);
            return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text, length);
        }

        internal byte[] Bytes(int column)
        {
            IntPtr blob = NativeMethods.sqlite3_column_blob(_statement, column);
            int length = NativeMethods.sqlite3_column_bytes(_statement, column);
            byte[] bytes = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(blob, bytes, 0, length);
            }

            return bytes;
        }
    }

    private static class NativeMethods
    {
        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_column_count(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}
