namespace MantlePlace.Core;

/// <summary>
/// Which of the two families a file belongs to (<c>spec/format.md</c> §4): the family settles the
/// frame its coordinates are in and the unit they are in.
/// </summary>
public enum FileFamily
{
    /// <summary>In neither family: a file with no coordinates (<c>Readme.md</c>, <c>Manifest.json</c>, <c>Terrain.mtl</c>).</summary>
    None,

    /// <summary>Absolute, in the order's CRS and the order's units.</summary>
    Georeferenced,

    /// <summary>Offsets in the local site frame, in metres on every order.</summary>
    HostReady,
}

/// <summary>The frame a file's coordinates are in, which its <see cref="FileFamily"/> settles.</summary>
public enum FileCoordinates
{
    /// <summary>A file with no coordinates.</summary>
    None,

    /// <summary>Absolute, in the order's CRS: the georeferenced frame.</summary>
    Order,

    /// <summary>Offsets from the site origin: the local site frame.</summary>
    Site,
}

/// <summary>Why the reader names a file it does not read.</summary>
public enum SkipCause
{
    /// <summary>The catalog names it and the download holds no such file. Reported, never fatal.</summary>
    FileMissing,

    /// <summary>The manifest lists it among the promised files this download does not hold.</summary>
    NotHeld,
}

/// <summary>Why a reading stopped at the manifest.</summary>
public enum BundleRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>A major above the newest this reader reads: refused before any file is read, with the update remedy.</summary>
    MajorAhead,

    /// <summary>Any other refusal; <see cref="BundleManifest.Error"/> says which.</summary>
    Unreadable,
}

/// <summary>
/// A file the reader found: a catalog name that the bundle holds, with what the manifest states about it.
/// </summary>
/// <param name="Path">The catalog's key, as the catalog spells it.</param>
/// <param name="Family">The family the file belongs to.</param>
/// <param name="Count">
/// The count the catalog states for the file (rows, features, triangles or pixels), or <c>null</c> when
/// it states none. Never counted by the reader.
/// </param>
/// <param name="Sha256">The digest the catalog states, which a step verifies the file against, or <c>null</c>.</param>
public sealed record BundleFile(string Path, FileFamily Family, long? Count, string? Sha256)
{
    /// <summary>The frame the file's coordinates are in: the one its family settles.</summary>
    public FileCoordinates Frame => Family switch
    {
        FileFamily.Georeferenced => FileCoordinates.Order,
        FileFamily.HostReady => FileCoordinates.Site,
        _ => FileCoordinates.None,
    };

    /// <summary>
    /// The unit of a 3.x file, read from the frame its family is in (<c>spec/format.md</c> §4, §9.4): a
    /// host-ready file's is the local site frame's unit, and a georeferenced file's is the georeferenced
    /// frame's horizontal unit. <see cref="LinearUnit.Unspecified"/> for a file in neither family, and
    /// for a 2.x file, which states its own.
    /// </summary>
    public LinearUnit Unit { get; init; }

    /// <summary>
    /// What a 3.x catalog states about a raster (<c>spec/format.md</c> §9.5): its pixel size, the frame
    /// its rectangle is in and that rectangle. <c>null</c> for a file that is not a raster, and for a
    /// 2.x file.
    /// </summary>
    public RasterGrid? Raster { get; init; }
}

/// <summary>
/// A raster as its 3.x catalog entry states it, every value read and none derived.
/// </summary>
/// <param name="Width">Its width in pixels, or 0 when the entry states none.</param>
/// <param name="Height">Its height in pixels, or 0 when the entry states none.</param>
/// <param name="Frame">The frame its rectangle is in, as stated (<c>local</c> for a host-ready PNG), or <c>null</c>.</param>
/// <param name="Rectangle">The rectangle it covers in that frame, or <c>null</c> when the entry states none.</param>
public sealed record RasterGrid(int Width, int Height, string? Frame, RasterRectangle? Rectangle);

/// <summary>The rectangle a raster covers in its frame, west, south, east and north, in that frame's unit.</summary>
public sealed record RasterRectangle(double Left, double Bottom, double Right, double Top);

/// <summary>A file the reader names and does not read, and why.</summary>
/// <param name="Path">The path the catalog or the not-held list names.</param>
/// <param name="Why">Why it is not read.</param>
/// <param name="Reason">
/// For a file not held, the reason the manifest states, verbatim: shown, never branched on, since the
/// reasons grow. <c>null</c> for a file the catalog names and the bundle lacks.
/// </param>
public sealed record SkippedFile(string Path, SkipCause Why, string? Reason);

/// <summary>
/// What a reader of either major finds in one bundle: where its manifest was, its major and level, each
/// file found by its catalog name and each file named and not read. The same for every host
/// (<c>tools/manifest-conformance/corpus/README.md</c>, "The reader group").
/// </summary>
public sealed class BundleContents
{
    /// <summary>The major read from the manifest's version, or 0 when it states none this reader can parse.</summary>
    public int Major { get; init; }

    /// <summary>Where the manifest was found, relative to the bundle's root.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>The download's level as the manifest states it once; <c>null</c> for a bundle before 3.0, or a refusal.</summary>
    public FidelityLevel? Level { get; init; }

    /// <summary>
    /// The two frames a 3.x manifest states once (<c>spec/format.md</c> §9.4), read from it and applied
    /// verbatim by a host; <c>null</c> for a bundle before 3.0, which states its frames per file, or a refusal.
    /// </summary>
    public BundleFrames? Frames { get; init; }

    /// <summary>Why the reading stopped at the manifest, or <see cref="BundleRefusal.None"/>.</summary>
    public BundleRefusal Refusal { get; init; }

    /// <summary>Each file found, sorted by path. Empty on a refusal.</summary>
    public IReadOnlyList<BundleFile> Files { get; init; } = [];

    /// <summary>Each file named and not read, sorted by path. Empty on a refusal.</summary>
    public IReadOnlyList<SkippedFile> Skipped { get; init; } = [];

    /// <summary>
    /// The found file the catalog names <paramref name="path"/>, matched regardless of case, or
    /// <c>null</c>: a name the catalog lacks is a file this download does not hold, not an error.
    /// </summary>
    public BundleFile? Find(string path) =>
        Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
}
