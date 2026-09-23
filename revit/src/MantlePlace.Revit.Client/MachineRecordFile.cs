namespace MantlePlace.Revit.Client;

/// <summary>
/// One small file per machine that every Revit process on it reads and changes: a read-decide-write
/// under an exclusive open of the file itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per machine, not per Revit version.</b> Revit 2025 and 2027 open side by side act for
/// the same curator; a record each would let both act on the same order. The exclusive open is the
/// whole of the locking: the first process to open the file decides, and the other reads the file
/// as the first one left it.
/// </para>
/// <para>
/// The new bytes are made before the file is truncated, so only a failure of the write itself can
/// leave the file empty or half-written. What a reader makes of such a file is its own decision.
/// </para>
/// </remarks>
public sealed class MachineRecordFile
{
    private const int LockAttempts = 10;
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(50);

    private readonly string _path;

    public MachineRecordFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>A record named <paramref name="name"/> beside the bundle cache, under the curator's local app data.</summary>
    public static string InLocalAppData(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        name);

    /// <summary>
    /// Hands the file's bytes to <paramref name="change"/> — empty for a file that does not exist yet —
    /// and writes the bytes it returns, all under one exclusive open.
    /// </summary>
    /// <exception cref="IOException">The file stayed locked, or could not be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder refused the file.</exception>
    public T Change<T>(Func<byte[], (byte[] Bytes, T Result)> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        using FileStream file = OpenExclusive();
        byte[] before = new byte[file.Length];
        file.ReadExactly(before);

        (byte[] after, T result) = change(before);
        file.Position = 0;
        file.SetLength(0);
        file.Write(after);
        return result;
    }

    private FileStream OpenExclusive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (attempt < LockAttempts && IsHeldByAnother(ex))
            {
                // The other Revit is mid-change; a change is a few milliseconds.
                Thread.Sleep(LockRetry);
            }
        }
    }

    /// <summary>A sharing or lock violation: another process has the file. Nothing else is retried.</summary>
    private static bool IsHeldByAnother(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
