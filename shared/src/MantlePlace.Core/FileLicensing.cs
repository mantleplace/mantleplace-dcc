namespace MantlePlace.Core;

public enum OdblRole
{
    Database,

    ProducedWork,

    None,
}

public sealed record FileSource(string Name, string License);

public sealed class FileLicensing
{
    public OdblRole Role { get; init; } = OdblRole.Database;

    public string License { get; init; } = string.Empty;

    public IReadOnlyList<FileSource> Sources { get; init; } = [];
}
