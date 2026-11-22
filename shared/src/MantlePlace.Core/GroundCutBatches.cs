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
        for (int index = 0; index < cuts.Count; index++)
        {
            GroundCut cut = cuts[index];
            Extent extent = Extent.Of(cut.Outer.Vertices);
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

    private readonly record struct Extent(double West, double South, double East, double North)
    {
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
