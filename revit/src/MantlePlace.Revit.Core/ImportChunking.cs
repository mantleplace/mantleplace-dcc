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
/// A step that creates one element per published row — the trees and the context buildings — used to
/// create them all inside one transaction, so the whole layer was one uninterruptible call
/// and Revit reported "not responding" for as long as it took. Cut into chunks, each chunk is one
/// commit and one <see cref="StagedImport"/> slice: Revit repaints between them, the import window
/// moves, and a Cancel is honoured at the next boundary with every committed chunk kept.
/// </para>
/// <para>
/// The subdivisions and the terrain are not chunked. Their cost is Revit rebuilding the ground's
/// element relations at commit, and in Revit 2025, on the one order it was probed on, chunking the
/// subdivisions did not lower it: the land cover committed one cut at a time took 1,491 s, against
/// 970 s and 1,783 s as one commit, and nearly all of it was one subdivision covering the whole
/// order. No chunk can be smaller than one subdivision (<see cref="SlowStepNotice"/>).
/// </para>
/// </remarks>
public static class ImportChunking
{
    /// <summary>How many elements one transaction creates.</summary>
    /// <remarks>
    /// <para>
    /// <b>The chunk is there for Cancel, not for speed.</b> A tree is a Planting family instance, and
    /// Revit charges for it at the commit: 7 to 11 ms per new tree in Revit 2025 and 2027 on a
    /// 19,755-tree order, depending on what else the machine was running. That cost is per tree, not
    /// per commit. In Revit 2027 the same trees committed 1,000 at a time took what they took 200 at a
    /// time (156 s of commits, against 165 s and 167 s), so a larger chunk saves nothing and only
    /// lengthens the wait below.
    /// </para>
    /// <para>
    /// Nor is the creation call a lever. A chunk created by one <c>NewFamilyInstances2</c> call, timed
    /// against one <c>NewFamilyInstance</c> per tree in alternating runs, moved the commit's cost per
    /// tree by less than the spread between repeat runs of either, in Revit 2025 and 2027. A
    /// batch-created instance also has no location until the commit regenerates it, so nothing tells
    /// its instances apart before the commit but the order the call returns them in.
    /// </para>
    /// <para>
    /// What the size does decide is how long Cancel and the import window wait on a commit: 200 trees
    /// commit in under a second at the start of the step and in several seconds near its end, as the
    /// project fills. The context buildings share the size; their commits were not timed apart. The
    /// import log times every commit (<c>[Mantle Place: vegetation] commit took …</c>), which is where
    /// a retune reads its numbers.
    /// </para>
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
