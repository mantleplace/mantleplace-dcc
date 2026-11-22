namespace MantlePlace.Core;

public enum FileFamily
{
    None,

    Georeferenced,

    HostReady,
}

public enum FileCoordinates
{
    None,

    Order,

    Site,
}

public enum SkipCause
{
    FileMissing,

    NotHeld,
}

public enum BundleRefusal
{
    None,

    MajorAhead,

    Unreadable,
}

public sealed record BundleFile(string Path, FileFamily Family, long? Count, string? Sha256)
{
    public FileCoordinates Frame => Family switch
    {
        FileFamily.Georeferenced => FileCoordinates.Order,
        FileFamily.HostReady => FileCoordinates.Site,
        _ => FileCoordinates.None,
    };

    public LinearUnit Unit { get; init; }

    public RasterGrid? Raster { get; init; }

    public IReadOnlyList<string> Tables { get; init; } = [];

    public string? FoliageTypeVocabulary { get; init; }
}

public sealed record RasterGrid(int Width, int Height, string? Frame, RasterRectangle? Rectangle);

public sealed record RasterRectangle(double Left, double Bottom, double Right, double Top);

public sealed record SkippedFile(string Path, SkipCause Why, string? Reason);

public sealed class BundleContents
{
    public int Major { get; init; }

    public required string ManifestPath { get; init; }

    public FidelityLevel? Level { get; init; }

    public BundleFrames? Frames { get; init; }

    public BundleRefusal Refusal { get; init; }

    public IReadOnlyList<BundleFile> Files { get; init; } = [];

    public IReadOnlyList<SkippedFile> Skipped { get; init; } = [];

    public BundleFile? Find(string path) =>
        Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
}
