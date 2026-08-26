namespace MantlePlace.Revit.Core;

/// <summary>
/// What the log says about the site-boundary subdivisions after the drape has tried to give each one
/// the aerial photograph.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This exists because a count was once the entire record.</b> A subdivision that refused the
/// drape incremented a counter, and Revit's own message went into the catch and nowhere else — so a
/// model with seventeen brown patches punched through the aerial photograph produced a log that
/// could not say why, and settling it cost two sessions and an eight-to-seventeen-minute import per
/// hypothesis. The sentence a curator reads is therefore built here, in the pure core, where it is
/// asserted by a test rather than by review (<c>HPS-02</c>).
/// </para>
/// <para>
/// The clause names the <em>consequence</em>, not just the number. "Kept their original look" reads
/// as cosmetic; "show through the photograph as untextured patches" is what is actually on screen,
/// and it is the difference between a line somebody skims and a line somebody reports.
/// </para>
/// </remarks>
public static class SubDivisionDrape
{
    /// <summary>
    /// The summary clause, or <c>null</c> when there were no subdivisions to speak of.
    /// </summary>
    /// <param name="draped">How many took the material and read it back.</param>
    /// <param name="refused">How many did not, for any reason.</param>
    /// <param name="reasons">
    /// The distinct reasons, in the caller's order. Empty is allowed and means the refusals produced
    /// no message — which is itself worth saying, because a refusal nobody can name is the exact
    /// shape of the defect this type was written for.
    /// </param>
    /// <returns>
    /// A clause beginning with <c>"; "</c> so it appends to the drape summary, or <c>null</c> when
    /// both counts are zero. Null rather than an empty string: "there were no subdivisions" and
    /// "there were subdivisions and nothing to report about them" are different, and only the caller
    /// can decide whether silence is right.
    /// </returns>
    public static string? Clause(int draped, int refused, IReadOnlyCollection<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(draped);
        ArgumentOutOfRangeException.ThrowIfNegative(refused);

        if (draped == 0 && refused == 0)
        {
            return null;
        }

        List<string> parts = [];

        if (draped > 0)
        {
            parts.Add($"the photograph also covers {draped:N0} site boundary subdivision(s)");
        }

        if (refused > 0)
        {
            // Said as what the curator will SEE. The subdivision is a hole in the drape, and a
            // sentence about a failed write does not tell anybody that.
            string clause = $"{refused:N0} subdivision(s) would not take it and show through the "
                + "photograph as untextured patches";

            clause += reasons.Count == 0
                ? " — Revit gave no reason, which is itself a defect worth reporting with this log"
                : $" — Revit said: {string.Join(" / ", reasons)}";

            parts.Add(clause);
        }

        return "; " + string.Join("; ", parts);
    }
}
