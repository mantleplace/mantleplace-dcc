using System.Globalization;
using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public sealed record ReadRecordLayer(PlannedFile File, IReadOnlyList<PublishedContour> Polylines);

public sealed record RefusedRecordLayer(string Name, string Why);

public sealed class AutoCadRecordSet
{
    public IReadOnlyList<ReadRecordLayer> Layers { get; private init; } = [];

    public IReadOnlyList<RefusedRecordLayer> Refused { get; private init; } = [];

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

    public string RefusalSentence() => string.Join(" ", Refused.Select(r => r.Why));
}
