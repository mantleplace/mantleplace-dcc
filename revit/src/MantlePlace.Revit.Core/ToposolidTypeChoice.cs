namespace MantlePlace.Revit.Core;

/// <summary>One toposolid type the project already contains, as the chooser needs to see it.</summary>
/// <param name="Id">Revit's <c>ElementId</c> value, carried as a number so the core stays Revit-free.</param>
/// <param name="TotalThickness">The compound structure's total width, in the caller's own unit.</param>
/// <param name="TopLayerThickness">
/// Layer 0's width, in the same unit. Distinct from <paramref name="TotalThickness"/> because the two
/// answer different questions: the total sets the clearance the base plane must leave, while layer 0
/// alone is what the drape splits. They coincide only for a single-layer type.
/// </param>
/// <param name="LayerCount">Zero when the type has no compound structure at all.</param>
/// <param name="HasStructuralLayer">
/// Whether any layer is a <c>Structure</c>. This is what separates a terrain type from a paving type.
/// </param>
public readonly record struct CandidateToposolidType(
    long Id,
    string Name,
    double TotalThickness,
    double TopLayerThickness,
    int LayerCount,
    bool HasStructuralLayer);

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
/// <b>The predicate is <see cref="DrapeLayering.Split"/> itself, over the same number the drape
/// splits</b> — layer 0's width — rather than a thickness threshold of its own. That is the point:
/// type choice and drape-splittability then cannot disagree, because they are the same function of
/// the same input. A separate threshold would eventually drift from it, and the symptom would be an
/// import that builds the terrain and then declines the aerial photograph with no obvious reason.
/// </para>
/// <para>
/// ⛔ <b>That is exactly what had drifted.</b> This predicate was written against the structure's
/// TOTAL width, and the drape then moved to splitting layer 0 alone (a multi-layer original keeps
/// every layer below it untouched) without the predicate following. For any multi-layer type the two
/// were different numbers, so the chooser could prefer a fat type whose top layer the drape then
/// refused — and a drape refusal rolls the WHOLE drape back. It never bit only because the type this
/// chooser picks in the metric template, "Generic - 1000mm", has one layer, where total and layer 0
/// are the same number. Hence <see cref="CandidateToposolidType.TopLayerThickness"/>: the claim above
/// is now true by construction instead of aspirationally.
/// </para>
/// <para>
/// ⛔ <b>The first cut is whether the type has a <c>Structure</c> layer</b>, and that came out of the
/// first probe rather than out of a guess. Thickness alone put a 150 mm <em>wood-plank path</em>
/// under the terrain — and on a re-import it put the plugin's own imagery-drape type there, because
/// that is thinner still. The metric template's layer functions separate the two cleanly:
/// "Generic - 1000mm", "Grassland - 1200mm" and "Water - 2000mm" all carry a <c>Structure</c> layer;
/// "Path - 150mm Wood Planks", "Path - 350mm Concrete" and the drape type this plugin derives are
/// all <c>Finish1</c>/<c>Substrate</c> only. So the rule excludes our own leftovers without matching
/// on our own name, which a locale or a rename would break.
/// </para>
/// <para>
/// Among the types that qualify the <em>thinnest</em> wins. Thickness is pure cost at this end: it
/// deepens the clearance the base plane must leave and buries the terrain under material nobody
/// asked for. Ties break by ordinal name so two runs of the same import choose the same type.
/// </para>
/// </remarks>
public static class ToposolidTypeChoice
{
    /// <summary>
    /// The type to build the terrain from, or <c>null</c> when the project has none usable.
    /// </summary>
    /// <remarks>
    /// Every preference is a tie-break rather than a filter, so a project that has only paving types
    /// still gets terrain. A type too thin for the drape makes a correct terrain anyway, and refusing
    /// to build ground at all because the photograph would not fit is the wrong trade — the drape's
    /// own refusal path already says so in the log when it happens.
    /// </remarks>
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

    /// <summary>
    /// The preference order, most significant first: is it ground, can the drape split it, is it
    /// thin, and finally the name so the answer never depends on collector order.
    /// </summary>
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

    /// <summary>
    /// Layer 0's width, not the total — the number <c>TryLayerImagery</c> actually hands
    /// <see cref="DrapeLayering.Split"/>. See the ⛔ paragraph on this class for what happens when
    /// these two drift apart.
    /// </summary>
    private static bool Splittable(CandidateToposolidType type, double minimumLayerThickness)
        => type.LayerCount > 0 && DrapeLayering.Split(type.TopLayerThickness, minimumLayerThickness).Ok;
}
