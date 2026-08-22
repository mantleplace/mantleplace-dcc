namespace MantlePlace.Revit.Core;

/// <summary>One toposolid type the project already contains, as the chooser needs to see it.</summary>
/// <param name="Id">Revit's <c>ElementId</c> value, carried as a number so the core stays Revit-free.</param>
/// <param name="TotalThickness">The compound structure's total width, in the caller's own unit.</param>
/// <param name="LayerCount">Zero when the type has no compound structure at all.</param>
public readonly record struct CandidateToposolidType(long Id, string Name, double TotalThickness, int LayerCount);

/// <summary>
/// Which of the project's toposolid types the terrain is built from. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Like <see cref="TerrainBasePlanner"/>, this replaces <c>FirstElementIdOf&lt;ToposolidType&gt;()</c>
/// — whichever type the collector enumerated first, with its thickness never read. Thickness is not
/// cosmetic here: it sets the clearance the base plane has to leave
/// (<see cref="TerrainBasePlanner.ClearanceFor"/>), and it decides whether the imagery drape can
/// split the type at all.
/// </para>
/// <para>
/// <b>The predicate is <see cref="DrapeLayering.Split"/> itself</b>, not a thickness threshold of its
/// own. That is the point: type choice and drape-splittability then cannot disagree, because they are
/// the same function. A separate threshold would eventually drift from it, and the symptom would be
/// an import that builds the terrain and then declines the aerial photograph with no obvious reason.
/// </para>
/// <para>
/// Among the types that qualify the <em>thinnest</em> wins. Thickness is pure cost at this end: it
/// deepens the clearance the base plane must leave and buries the terrain under material nobody asked
/// for. Ties break by ordinal name so two runs of the same import choose the same type.
/// </para>
/// </remarks>
public static class ToposolidTypeChoice
{
    /// <summary>
    /// The type to build the terrain from, or <c>null</c> when the project has none usable.
    /// </summary>
    /// <remarks>
    /// The fallback arm — thinnest with any positive thickness, when nothing is splittable — is
    /// deliberate. A type too thin for the drape still makes a correct terrain, and refusing to build
    /// terrain at all because the photograph would not fit is the wrong trade. The drape's own
    /// refusal path already says so in the log when it happens.
    /// </remarks>
    public static CandidateToposolidType? Best(
        IReadOnlyList<CandidateToposolidType> types,
        double minimumLayerThickness)
    {
        ArgumentNullException.ThrowIfNull(types);

        CandidateToposolidType? splittable = null;
        CandidateToposolidType? anyPositive = null;

        foreach (CandidateToposolidType type in types)
        {
            if (!double.IsFinite(type.TotalThickness) || type.TotalThickness <= 0.0)
            {
                continue;
            }

            if (IsThinner(type, anyPositive))
            {
                anyPositive = type;
            }

            if (type.LayerCount > 0
                && DrapeLayering.Split(type.TotalThickness, minimumLayerThickness).Ok
                && IsThinner(type, splittable))
            {
                splittable = type;
            }
        }

        return splittable ?? anyPositive;
    }

    private static bool IsThinner(CandidateToposolidType candidate, CandidateToposolidType? incumbent)
    {
        if (incumbent is not { } held)
        {
            return true;
        }

        int byThickness = candidate.TotalThickness.CompareTo(held.TotalThickness);
        return byThickness < 0
            || (byThickness == 0 && string.CompareOrdinal(candidate.Name, held.Name) < 0);
    }
}
