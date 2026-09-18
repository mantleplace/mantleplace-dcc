namespace MantlePlace.Revit.Core;

/// <summary>
/// The words on the two windows this plugin opens: their title bars, their headings and their
/// button faces.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than inline in the shim for the reason every other decision in this assembly is
/// (<c>HPS-02</c>, <c>HPS-42</c>): CI never builds the shim, so a string written there is checked by
/// review alone. What is left in the shim is assignment onto a <c>Window</c> and a <c>Button</c>.
/// </para>
/// <para>
/// <b>Title Case, because the host is Revit.</b> Ribbon face text already follows it — <c>Vault</c>,
/// <c>Import Bundle</c> — for the reason set out in <c>revit/CLAUDE.md</c>: every Autodesk tab beside
/// ours uses it, and a sentence-case verb phrase is what makes a plugin read as somebody's add-in.
/// The windows the ribbon opens had drifted off it — <c>Prepare for Revit</c> sat beside
/// <c>Remove download</c> in the same row — so the rule now covers them too.
/// </para>
/// <para>
/// The words themselves are shared with the reference host wherever the action is shared
/// (<c>HPS-51</c>); only the casing is Revit's. <c>Refresh</c> and <c>Import</c> are the vault
/// panel's own labels in Unreal, and <see cref="SignInHeading"/> is
/// <see cref="AccountRibbon.SignInFace"/> rather than a second spelling of it. <c>Vault</c> is one
/// of the words that table fixes; <c>Prepare for Revit</c> is this host's own, and is not.
/// </para>
/// <para>
/// Not named <c>WindowChrome</c>, which is what it is: WPF already has a
/// <c>System.Windows.Shell.WindowChrome</c>, for the non-client area of a window, and a shim file
/// that ever reaches for that one would get <c>CS0104</c> against this. Nothing in the shim builds in
/// CI, so that collision would surface on one developer's machine and nowhere else.
/// </para>
/// </remarks>
public static class WindowLabels
{
    /// <summary>
    /// The vault browser's title bar.
    /// </summary>
    /// <remarks>
    /// Was <c>Mantle Place — your vault</c>. An em dash inside a title bar is a typographic flourish
    /// that Windows truncates first and that no Autodesk window uses; product then purpose is what
    /// the rest of the host does.
    /// </remarks>
    public const string VaultWindowTitle = "Mantle Place Vault";

    /// <summary>The sign-in window's title bar. Was <c>Mantle Place — signing in</c>.</summary>
    public const string SignInWindowTitle = "Mantle Place Sign In";

    /// <summary>
    /// The vault browser's heading, beside the mark.
    /// </summary>
    /// <remarks>
    /// The purpose alone, not the product again: the mark is to its left and the title bar is above
    /// it, so "Mantle Place" here would be the third saying of it in one window. This is also the
    /// reference host's heading over the same list.
    /// </remarks>
    public const string VaultHeading = "Vault";

    /// <summary>The sign-in window's heading. The ribbon's own word for the action (<see cref="AccountRibbon.SignInFace"/>).</summary>
    public const string SignInHeading = "Sign In";

    /// <summary>Re-list the vault.</summary>
    public const string Refresh = "Refresh";

    /// <summary>Materialize this host's deliverables, then download them (<c>HPS-18</c>).</summary>
    public const string PrepareForRevit = "Prepare for Revit";

    /// <summary>Import the downloaded bundle into the active project. The one primary action here.</summary>
    public const string Import = "Import";

    /// <summary>Evict this order from the bundle cache (<c>HPS-44</c>). Was <c>Remove download</c>.</summary>
    public const string RemoveDownload = "Remove Download";

    /// <summary>Stop the step in flight. Never the primary action — it undoes rather than does.</summary>
    public const string Cancel = "Cancel";

    /// <summary>Dismiss a window whose work is over.</summary>
    public const string Close = "Close";
}
