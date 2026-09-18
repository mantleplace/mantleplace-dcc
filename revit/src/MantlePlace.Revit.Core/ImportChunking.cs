namespace MantlePlace.Revit.Core;

/// <summary>One transaction's share of a step's elements.</summary>
/// <param name="Start">Zero-based index of the chunk's first element.</param>
/// <param name="Count">How many elements the chunk holds. Never zero.</param>
public readonly record struct ImportChunk(int Start, int Count);

/// <summary>
/// How a step that creates many elements splits them into transactions. Pure.
/// </summary>
/// <remarks>
/// <para>
/// A step that creates one element per published row — the trees today, the context buildings next —
/// used to create them all inside one transaction, so the whole layer was one uninterruptible call
/// and Revit reported "not responding" for as long as it took. Cut into chunks, each chunk is one
/// commit and one <see cref="StagedImport"/> slice: Revit repaints between them, the import window
/// moves, and a Cancel is honoured at the next boundary with every committed chunk kept.
/// </para>
/// <para>
/// The subdivisions and the terrain are not chunked, and cannot usefully be: their cost is Revit
/// rebuilding the ground's element relations at commit, which is paid once per commit whatever it
/// holds, so splitting them multiplies the cost rather than dividing it.
/// </para>
/// </remarks>
public static class ImportChunking
{
    /// <summary>How many elements one transaction creates.</summary>
    /// <remarks>
    /// A tree is a two-solid DirectShape and costs milliseconds to create, so a chunk of this size
    /// commits in well under a second — short enough that Cancel answers without a visible wait, long
    /// enough that the per-transaction overhead is a small share of the step. The import log times
    /// every commit (<c>[Mantle Place: vegetation] commit took …</c>), which is where a retune reads
    /// its numbers.
    /// </remarks>
    public const int ElementsPerTransaction = 200;

    /// <summary>The chunks for <paramref name="count"/> elements, in order.</summary>
    public static IReadOnlyList<ImportChunk> Chunks(int count)
    {
        List<ImportChunk> chunks = [];
        for (int start = 0; start < count; start += ElementsPerTransaction)
        {
            chunks.Add(new ImportChunk(start, Math.Min(ElementsPerTransaction, count - start)));
        }

        return chunks;
    }
}
