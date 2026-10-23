using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MantlePlace.Revit.CarrierSpike;

/// <summary>
/// The SQLite Windows ships as <c>winsqlite3.dll</c> (System32, Windows 10 1607 and later), reached
/// by P/Invoke: no NuGet package, no native binary shipped beside the add-in, nothing for another
/// add-in's SQLite to collide with in Revit's process. The handful of calls a read-only GeoPackage
/// read needs, plus the one a test fixture needs to write one.
/// </summary>
public static class WinSqlite
{
    private const string Library = "winsqlite3.dll";

    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadOnly = 0x0000_0001;
    private const int OpenReadWrite = 0x0000_0002;
    private const int OpenCreate = 0x0000_0004;

    public const int Integer = 1;
    public const int Float = 2;
    public const int Text = 3;
    public const int Blob = 4;
    public const int Null = 5;

    /// <summary>Runs every row of <paramref name="sql"/> against a read-only open of <paramref name="path"/>.</summary>
    /// <param name="onRow">Called once per row with that row's reader.</param>
    public static void Query(string path, string sql, Action<RowReader> onRow)
    {
        ArgumentNullException.ThrowIfNull(onRow);
        IntPtr db = Open(path, OpenReadOnly);
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

    /// <summary>Creates <paramref name="path"/> if need be and runs a script of statements in it.</summary>
    public static void ExecuteScript(string path, string sql)
    {
        IntPtr db = Open(path, OpenReadWrite | OpenCreate);
        try
        {
            Check(db, NativeMethods.sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out IntPtr error));
            if (error != IntPtr.Zero)
            {
                NativeMethods.sqlite3_free(error);
            }
        }
        finally
        {
            _ = NativeMethods.sqlite3_close_v2(db);
        }
    }

    /// <summary>The version string of the SQLite the OS loaded, for the probe's record.</summary>
    public static string Version => Marshal.PtrToStringUTF8(NativeMethods.sqlite3_libversion()) ?? string.Empty;

    private static IntPtr Open(string path, int flags)
    {
        int code = NativeMethods.sqlite3_open_v2(Utf8(path), out IntPtr db, flags, IntPtr.Zero);
        if (code != Ok)
        {
            string message = db == IntPtr.Zero ? $"code {code}" : Message(db);
            _ = NativeMethods.sqlite3_close_v2(db);
            throw new IOException($"SQLite could not open {path}: {message}");
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
    public sealed class RowReader
    {
        private readonly IntPtr _statement;

        internal RowReader(IntPtr statement) => _statement = statement;

        public int ColumnCount => NativeMethods.sqlite3_column_count(_statement);

        public string ColumnName(int column) => Marshal.PtrToStringUTF8(NativeMethods.sqlite3_column_name(_statement, column)) ?? string.Empty;

        public int Type(int column) => NativeMethods.sqlite3_column_type(_statement, column);

        public double Double(int column) => NativeMethods.sqlite3_column_double(_statement, column);

        public long Int64(int column) => NativeMethods.sqlite3_column_int64(_statement, column);

        public string String(int column)
        {
            IntPtr text = NativeMethods.sqlite3_column_text(_statement, column);
            int length = NativeMethods.sqlite3_column_bytes(_statement, column);
            return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text, length);
        }

        public byte[] Bytes(int column)
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
        internal static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern void sqlite3_free(IntPtr pointer);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_libversion();

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
