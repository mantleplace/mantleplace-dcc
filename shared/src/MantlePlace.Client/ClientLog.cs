namespace MantlePlace.Client;

public static class ClientLog
{
    private static readonly AsyncLocal<Action<string>?> Redirected = new();

    private static readonly Lazy<ConnectedHostLog> File = new(() => new ConnectedHostLog(DefaultPath));

    public static string DefaultPath => MachineRecordFile.InLocalAppData("client.log");

    public static void Write(string line)
    {
        if (Redirected.Value is { } sink)
        {
            sink(line);
            return;
        }

        File.Value.Append(line);
    }

    public static IDisposable RedirectTo(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Action<string>? previous = Redirected.Value;
        Redirected.Value = sink;
        return new Restore(previous);
    }

    private sealed class Restore(Action<string>? previous) : IDisposable
    {
        public void Dispose() => Redirected.Value = previous;
    }
}
