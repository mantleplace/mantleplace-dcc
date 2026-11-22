namespace MantlePlace.Core;

public static class GroundCutBatches
{
    public const int MostCuts = 50;

    public const double GapMetres = 1.0;

    public static IReadOnlyList<IReadOnlyList<int>> Of(IReadOnlyList<GroundCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);

        List<List<int>> batches = [];
        List<List<Extent>> extents = [];
        for (int index = 0; index < cuts.Count; index++)
        {
            Extent extent = Extent.Of(cuts[index].Outer.Vertices);
            int open = extents.FindIndex(held => held.Count < MostCuts && held.TrueForAll(other => other.IsClearOf(extent)));
            if (open < 0)
            {
                batches.Add([]);
                extents.Add([]);
                open = batches.Count - 1;
            }

            batches[open].Add(index);
            extents[open].Add(extent);
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
