namespace MantlePlace.Revit.Core;

/// <summary>Exact conversions for the three linear units bundle artifacts are delivered in.</summary>
/// <remarks>
/// Both foot definitions are exact by statute — the US survey foot is
/// 1200/3937 m and the international foot is 0.3048 m.
/// They differ by about 2 ppm, which is ~3 mm across a 1.4 km AOI: harmless for placement, and
/// wrong enough to matter if the two are ever conflated in a survey deliverable. Writing both out
/// exactly costs nothing and removes the temptation to round.
/// </remarks>
public static class LinearUnits
{
    /// <summary>Metres per one unit of <paramref name="unit"/>.</summary>
    /// <remarks><see cref="LinearUnit.Unspecified"/> is metric — that is what every bundle cut
    /// before the <c>delivery</c> block existed was.</remarks>
    public static double MetresPerUnit(LinearUnit unit) => unit switch
    {
        LinearUnit.UsSurveyFoot => 1200.0 / 3937.0,
        LinearUnit.InternationalFoot => 0.3048,
        _ => 1.0,
    };

    /// <summary>The raw manifest spelling, for messages and round-tripping.</summary>
    public static string ToManifestToken(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => "m",
        LinearUnit.UsSurveyFoot => "ftUS",
        LinearUnit.InternationalFoot => "ft",
        _ => string.Empty,
    };
}
