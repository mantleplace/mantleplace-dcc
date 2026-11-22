namespace MantlePlace.Core;

public readonly record struct CandidateToposolidType(
    long Id,
    string Name,
    double TotalThickness,
    double TopLayerThickness,
    int LayerCount,
    bool HasStructuralLayer);

public static class ToposolidTypeChoice
{
    public static CandidateToposolidType? Best(
        IReadOnlyList<CandidateToposolidType> types,
        double minimumLayerThickness)
    {
        ArgumentNullException.ThrowIfNull(types);

        CandidateToposolidType? best = null;
        foreach (CandidateToposolidType type in types)
        {
            if (!double.IsFinite(type.TotalThickness) || type.TotalThickness <= 0.0)
            {
                continue;
            }

            if (best is not { } incumbent || Beats(type, incumbent, minimumLayerThickness))
            {
                best = type;
            }
        }

        return best;
    }

    private static bool Beats(
        CandidateToposolidType candidate,
        CandidateToposolidType incumbent,
        double minimumLayerThickness)
    {
        if (candidate.HasStructuralLayer != incumbent.HasStructuralLayer)
        {
            return candidate.HasStructuralLayer;
        }

        bool candidateSplits = Splittable(candidate, minimumLayerThickness);
        bool incumbentSplits = Splittable(incumbent, minimumLayerThickness);
        if (candidateSplits != incumbentSplits)
        {
            return candidateSplits;
        }

        int byThickness = candidate.TotalThickness.CompareTo(incumbent.TotalThickness);
        return byThickness < 0
            || (byThickness == 0 && string.CompareOrdinal(candidate.Name, incumbent.Name) < 0);
    }

    private static bool Splittable(CandidateToposolidType type, double minimumLayerThickness)
        => type.LayerCount > 0 && DrapeLayering.Split(type.TopLayerThickness, minimumLayerThickness).Ok;
}
