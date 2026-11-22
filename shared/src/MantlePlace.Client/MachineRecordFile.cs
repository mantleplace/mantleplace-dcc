namespace MantlePlace.Client;

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

    public static string InLocalAppData(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        name);

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
                Thread.Sleep(LockRetry);
            }
        }
    }

    private static bool IsHeldByAnother(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
