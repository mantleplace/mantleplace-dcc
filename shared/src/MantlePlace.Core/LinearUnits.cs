namespace MantlePlace.Core;

public static class LinearUnits
{
    public static double MetresPerUnit(LinearUnit unit) => unit switch
    {
        LinearUnit.UsSurveyFoot => 1200.0 / 3937.0,
        LinearUnit.InternationalFoot => 0.3048,
        _ => 1.0,
    };

    public static string ToManifestToken(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => "m",
        LinearUnit.UsSurveyFoot => "ftUS",
        LinearUnit.InternationalFoot => "ft",
        _ => string.Empty,
    };
}
