namespace MantlePlace.Revit.Core;

/// <summary>
/// Where the "Import bundle zip" command gets its path, and where it reports when nobody is looking.
/// </summary>
/// <remarks>
/// <para>
/// Every entry point into the import used to be UI-gated: the command opens an
/// <c>OpenFileDialog</c> before it parses anything, and that dialog is created by the add-in rather
/// than by Revit, so Revit's journal never records it and journal playback cannot drive the import
/// at all. The consequence
/// was not academic — <c>Toposolid.Create</c>, <c>RevitLinkType.CreateFromIFC</c> and
/// <c>ProjectLocation.SetProjectPosition</c> compile but had never executed inside Revit.
/// </para>
/// <para>
/// So the path may come from the environment instead, which mirrors the split the reference host
/// already has (<c>ImportVaultPackage(path)</c> is callable; <c>BrowseForVaultZip()</c> is a
/// separate thing). The decision lives here rather than in the shim because the shim is never built
/// in CI — an env-var rule written there would be covered by review alone (HPS-02, HPS-42).
/// </para>
/// </remarks>
public static class LocalBundleSource
{
    /// <summary>The environment variable a journal or tester script sets to run unattended.</summary>
    public const string PathVariable = "MANTLEPLACE_BUNDLE_ZIP";

    /// <summary>
    /// The zip an unattended run named, or <c>null</c> to ask the user.
    /// </summary>
    /// <remarks>
    /// A blank or whitespace value reads as "not set" rather than as an empty path: a shell that
    /// exports the variable with nothing in it must fall back to the picker, not fail on a path
    /// that cannot exist.
    /// </remarks>
    public static string? Unattended(string? variableValue)
        => string.IsNullOrWhiteSpace(variableValue) ? null : variableValue.Trim();

    /// <summary>
    /// Where an unattended run writes what it did — beside the zip it was handed.
    /// </summary>
    /// <remarks>
    /// An unattended run must not raise a <c>TaskDialog</c>: nothing is there to dismiss it, and
    /// journal playback blocks on it forever. A file beside the input is what the caller that set
    /// <see cref="PathVariable"/> already knows how to find.
    /// </remarks>
    public static string LogPathFor(string zipPath) => zipPath + ".mantleplace-import.log";
}
