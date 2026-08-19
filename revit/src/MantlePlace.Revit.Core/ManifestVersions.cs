namespace MantlePlace.Revit.Core;

/// <summary>
/// The Revit host's bundle-manifest version floor — and the single place it is written down.
/// </summary>
/// <remarks>
/// <para>
/// Clean break (HPS-31): this host supports exactly one manifest version and refuses everything
/// below it. There is no fallback ladder and no dual-parsing; an old bundle is re-procured from the
/// vault, not re-interpreted here.
/// </para>
/// <para>
/// ONE HOME FOR THE NUMBER. The manifest reader's version gate, the conformance suite's
/// cross-check, and <c>tools/manifest-conformance/verified-against.json</c> all resolve to this
/// constant. The gate reads it by regexing this file — the <c>revit.floorSource</c> entry declares
/// the path and the pattern (HPS-39), so renaming the constant or moving this file means editing
/// that entry in the same commit. The Unreal counterpart is
/// <c>MantlePlaceMinSupportedManifestVersion</c> in
/// <c>unreal/Plugins/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h</c>.
/// </para>
/// <para>
/// Raising it is a three-move change: bump the number here, teach the corpus the new accept shape,
/// and refresh the <c>revit</c> entry's <c>evidence</c> prose in <c>verified-against.json</c>.
/// </para>
/// </remarks>
public static class ManifestVersions
{
    /// <summary>The oldest bundle-manifest version this plugin will import.</summary>
    public const int MinSupportedManifestVersion = 18;
}
