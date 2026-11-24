namespace MantlePlace.Core;

public static class GroundCutBatches
{
    public const int MostCuts = 50;

    public const int MostVertices = 1_000;

    public const double GapMetres = 1.0;

    public static IReadOnlyList<IReadOnlyList<int>> Of(IReadOnlyList<GroundCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);

        List<List<int>> batches = [];
        List<List<Extent>> extents = [];
        List<int> vertices = [];
        Extent[] all = [.. cuts.Select(cut => Extent.Of(cut.Outer.Vertices))];
        foreach (int index in LocalOrder(all))
        {
            GroundCut cut = cuts[index];
            Extent extent = all[index];
            int weight = cut.Outer.Vertices.Count + cut.Holes.Sum(hole => hole.Vertices.Count);
            int open = Enumerable.Range(0, batches.Count).FirstOrDefault(
                candidate => batches[candidate].Count < MostCuts
                    && vertices[candidate] + weight <= MostVertices
                    && extents[candidate].TrueForAll(other => other.IsClearOf(extent)),
                -1);
            if (open < 0)
            {
                batches.Add([]);
                extents.Add([]);
                vertices.Add(0);
                open = batches.Count - 1;
            }

            batches[open].Add(index);
            extents[open].Add(extent);
            vertices[open] += weight;
        }

        return batches;
    }

    private static IEnumerable<int> LocalOrder(Extent[] extents)
    {
        Extent[] finite = [.. extents.Where(extent => extent.IsFinite)];
        if (finite.Length == 0)
        {
            return Enumerable.Range(0, extents.Length);
        }

        double west = finite.Min(extent => extent.West);
        double south = finite.Min(extent => extent.South);
        double side = Math.Max(finite.Max(extent => extent.East) - west, finite.Max(extent => extent.North) - south);
        double scale = side > 0 ? ushort.MaxValue / side : 0;

        uint Key(Extent extent) => extent.IsFinite
            ? Interleave(Quantise(((extent.West + extent.East) / 2) - west, scale))
                | (Interleave(Quantise(((extent.South + extent.North) / 2) - south, scale)) << 1)
            : 0;

        return Enumerable.Range(0, extents.Length).OrderBy(index => Key(extents[index])).ThenBy(index => index);
    }

    private static uint Quantise(double offset, double scale) => (uint)Math.Clamp(offset * scale, 0, ushort.MaxValue);

    private static uint Interleave(uint value)
    {
        value &= 0xFFFF;
        value = (value | (value << 8)) & 0x00FF00FF;
        value = (value | (value << 4)) & 0x0F0F0F0F;
        value = (value | (value << 2)) & 0x33333333;
        return (value | (value << 1)) & 0x55555555;
    }

    private readonly record struct Extent(double West, double South, double East, double North)
    {
        public bool IsFinite => double.IsFinite(West) && double.IsFinite(South) && double.IsFinite(East) && double.IsFinite(North);

        public static Extent Of(IReadOnlyList<SiteVertex> vertices) => vertices.Count == 0
            ? new(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity)
            : new(
                vertices.Min(vertex => vertex.EastM),
                vertices.Min(vertex => vertex.NorthM),
                vertices.Max(vertex => vertex.EastM),
                vertices.Max(vertex => vertex.NorthM));

        public bool IsClearOf(Extent other)
            => other.West >= East + GapMetres
                || West >= other.East + GapMetres
                || other.South >= North + GapMetres
                || South >= other.North + GapMetres;
    }
}
