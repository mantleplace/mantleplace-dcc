using System.Globalization;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad;

/// <summary>A public-record layer whose file was read: the layer it goes on and its polylines.</summary>
/// <param name="File">The planned file.</param>
/// <param name="Polylines">Its polylines, in file order.</param>
public sealed record ReadRecordLayer(PlannedFile File, IReadOnlyList<PublishedContour> Polylines);

/// <summary>A public-record layer left out of the import, and why.</summary>
/// <param name="Name">The record layer's name, as the block states it.</param>
/// <param name="Why">The reader's reason, which names the file.</param>
public sealed record RefusedRecordLayer(string Name, string Why);

/// <summary>
/// The public-record layers read file by file. Pure: the caller opens each file.
/// </summary>
/// <remarks>
/// A record DXF the reader cannot place exactly (the platform's contamination and historic-places files
/// hold <c>POINT</c> entities, which it refuses by name) is refused on its own. The other record layers are
/// still read and placed, and the user is told which layer was left out and why. Every file is read before
/// anything is drawn, so a refused file never leaves a layer half placed.
/// </remarks>
public sealed class AutoCadRecordSet
{
    /// <summary>The layers that read, in the order the block lists them.</summary>
    public IReadOnlyList<ReadRecordLayer> Layers { get; private init; } = [];

    /// <summary>The layers left out, in the order the block lists them.</summary>
    public IReadOnlyList<RefusedRecordLayer> Refused { get; private init; } = [];

    /// <summary>Reads each file apart from the others.</summary>
    /// <param name="files">The record layers' planned files.</param>
    /// <param name="open">Opens one file's text.</param>
    public static AutoCadRecordSet Read(IReadOnlyList<PlannedFile> files, Func<PlannedFile, TextReader> open)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(open);

        List<ReadRecordLayer> layers = [];
        List<RefusedRecordLayer> refused = [];
        foreach (PlannedFile file in files)
        {
            using TextReader reader = open(file);
            if (PublishedContourReader.TryParse(reader, file.Name, out IReadOnlyList<PublishedContour>? polylines, out _) is { } why)
            {
                refused.Add(new RefusedRecordLayer(file.Name, why));
            }
            else
            {
                layers.Add(new ReadRecordLayer(file, polylines!));
            }
        }

        return new AutoCadRecordSet { Layers = layers, Refused = refused };
    }

    /// <summary>Why a set with no readable layer places nothing: each layer's reason, joined.</summary>
    public string RefusalSentence() => string.Join(" ", Refused.Select(r => r.Why));
}
