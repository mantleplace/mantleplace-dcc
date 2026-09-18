namespace MantlePlace.Revit.Core;

/// <summary>The names of the drape materials a subdivision can wear. Pure.</summary>
/// <remarks>
/// <para>
/// Under smooth shading every subdivision's photograph is anchored to its own bounding-box corner,
/// and one material carries one offset, so every subdivision has a material of its own
/// (<see cref="PerSubDivision"/>). Under flat shading the anchor is the project origin, the ground's
/// own material fits every subdivision, and a keyword needs only one extra material per keyword
/// (<see cref="Shared"/>).
/// </para>
/// <para>
/// Every name is derived from the stamp and the subtype and from nothing this session invented, so
/// a re-import resolves the material an earlier import made rather than growing another. A name
/// with no keyword is exactly the name this plugin wrote before keywords existed, for the same
/// reason.
/// </para>
/// </remarks>
public static class GroundMaterialNames
{
    /// <summary>Characters Revit refuses in an element name. Anything else in a feature name is kept.</summary>
    private const string Refused = @"\:{}[]|;<>?`~";

    /// <summary>
    /// The material every subdivision with this keyword shares under flat shading:
    /// <c>{imagery} {keyword}</c>, or the ground's own material name when there is no keyword.
    /// </summary>
    public static string Shared(string imageryName, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(imageryName);
        return WithKeyword(imageryName, keyword);
    }

    /// <summary>
    /// One subdivision's own material under smooth shading:
    /// <c>{imagery} boundary {token} {keyword}</c> for land use and
    /// <c>{imagery} land cover {token} {keyword}</c> for land cover.
    /// </summary>
    /// <param name="imageryName">The ground's drape material name (<see cref="DrapeLayering.ImageryName"/>).</param>
    /// <param name="layer">Which layer the subdivision was cut from. Both stamp unnamed features by
    /// position, so without it land-use 1 and land-cover 1 would share one material and one offset.</param>
    /// <param name="token">The stamp's per-feature token (<see cref="SiteBoundaryIdentity.Parse"/>).</param>
    /// <param name="keyword">The renderer phrase, or <c>null</c>.</param>
    public static string PerSubDivision(string imageryName, GroundLayer layer, string token, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(imageryName);
        ArgumentNullException.ThrowIfNull(token);

        string safe = string.Concat(token.Select(c => Refused.Contains(c) ? '-' : c));
        return WithKeyword($"{imageryName} {GroundLayerWords.For(layer).MaterialKind} {safe}", keyword);
    }

    /// <summary>The keyword goes last, so it is never split by anything the bundle names.</summary>
    private static string WithKeyword(string name, string? keyword)
        => string.IsNullOrEmpty(keyword) ? name : name + " " + keyword;
}
