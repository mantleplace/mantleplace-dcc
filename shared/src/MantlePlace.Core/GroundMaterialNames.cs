namespace MantlePlace.Core;

public static class GroundMaterialNames
{
    private const string Refused = @"\:{}[]|;<>?`~";

    public static string Shared(string imageryName, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(imageryName);
        return WithKeyword(imageryName, keyword);
    }

    public static string PerSubDivision(string imageryName, GroundLayer layer, string token, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(imageryName);
        ArgumentNullException.ThrowIfNull(token);

        string safe = string.Concat(token.Select(c => Refused.Contains(c) ? '-' : c));
        return WithKeyword($"{imageryName} {GroundLayerWords.For(layer).MaterialKind} {safe}", keyword);
    }

    private static string WithKeyword(string name, string? keyword)
        => string.IsNullOrEmpty(keyword) ? name : name + " " + keyword;
}
