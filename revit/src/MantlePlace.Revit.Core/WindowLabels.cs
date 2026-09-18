using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The words on the windows this plugin opens — the vault, sign-in and import windows: their title
/// bars, their headings, their button faces and, for the import window, its step rows and their
/// states.
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

    /// <summary>
    /// Import the downloaded bundle into the active project — the vault window's primary action, and
    /// the import window's once the checklist is set: the same act, so the same word.
    /// </summary>
    public const string Import = "Import";

    /// <summary>Evict this order from the bundle cache (<c>HPS-44</c>). Was <c>Remove download</c>.</summary>
    public const string RemoveDownload = "Remove Download";

    /// <summary>Stop the step in flight. Never the primary action — it undoes rather than does.</summary>
    public const string Cancel = "Cancel";

    /// <summary>Dismiss a window whose work is over.</summary>
    public const string Close = "Close";

    /// <summary>The import window's title bar.</summary>
    public const string ImportWindowTitle = "Mantle Place Bundle Import";

    /// <summary>
    /// The import window's heading: the glossary's name for the action, and <c>HPS-51</c>'s word for
    /// the window that shows it running.
    /// </summary>
    public const string ImportHeading = "Bundle Import";

    /// <summary>A step's state, as its row in the import window says it (<c>HPS-51</c>).</summary>
    public static string StateWord(ImportStepState state) => state switch
    {
        ImportStepState.Waiting => "Waiting",
        ImportStepState.Importing => "Importing",
        ImportStepState.Done => "Done",
        ImportStepState.Failed => "Failed",
        ImportStepState.Cancelled => "Cancelled",
        ImportStepState.NotRun => "Not Run",
        _ => state.ToString(),
    };

    /// <summary>
    /// A step's row in the import window, and its name in the log's closing line.
    /// </summary>
    /// <remarks>
    /// The glossary's words, not the planner's: a curator waits on "Terrain", not on
    /// <c>ToposurfaceFromSurfaceTin</c>, and three kinds that each build the one terrain are one row
    /// to them. The land-use and land-cover polygons are both <c>Subdivisions</c>, which is what they
    /// become (<c>CONTEXT.md</c>), told apart by the layer they were cut from; the IFC is the
    /// <c>Site Model</c>.
    /// </remarks>
    public static string StepName(ImportStepKind kind) => kind switch
    {
        ImportStepKind.SetSharedCoordinates => "Shared Coordinates",
        _ when ImportLayers.Of(kind) is { } layer => LayerName(layer),

        // A kind added to the planner and never named here still gets a row rather than a throw on
        // Revit's thread; the test that walks the enum is what makes it get a real name.
        _ => kind.ToString(),
    };

    /// <summary>
    /// A layer's row in the checklist, and the name of the step that builds it — one word for one
    /// thing, before the import and during it.
    /// </summary>
    public static string LayerName(ImportLayer layer) => layer switch
    {
        ImportLayer.Terrain => "Terrain",
        ImportLayer.SiteModel => "Site Model",
        ImportLayer.RoadCentrelines => "Road Centrelines",
        ImportLayer.LandUseSubdivisions => "Land Use Subdivisions",
        ImportLayer.LandCoverSubdivisions => "Land Cover Subdivisions",
        ImportLayer.Trees => "Trees",
        ImportLayer.ImageryDrape => "Imagery Drape",
        _ => layer.ToString(),
    };

    /// <summary>
    /// The heading over the checklist, before the steps run (<c>HPS-51</c>).
    /// </summary>
    /// <remarks>
    /// Not <c>Layers</c>, though that is what the rows are to the people who built this. The glossary
    /// keeps <em>layer</em> off the user's page — a landscape layer block, a paint layer and a vector
    /// layer are three different things already — and the reference host, whose picker is still to
    /// come, has landscape layers and data layers of its own on the same screen.
    /// </remarks>
    public const string IncludeHeading = "Include";

    /// <summary>
    /// What a disabled box says beside it: the layer it cannot be imported without (<c>HPS-51</c>).
    /// </summary>
    public static string Needs(ImportLayer prerequisite) => $"Needs {LayerName(prerequisite)}";

    /// <summary>How far a chunked step has got, in the elements it creates: <c>250 of 600</c>.</summary>
    public static string ProgressText(StepProgress progress)
        => string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0}", progress.Done, progress.Total);

    /// <summary>
    /// The import window's status line: the step in flight, how far a chunked one has got, and a
    /// cancel that is waiting for its boundary. Empty between steps.
    /// </summary>
    /// <remarks>
    /// A step that is one commit shows its name and nothing else, because there is no part of a
    /// commit to count; <see cref="SlowStepNotice"/> is where the log says so.
    /// </remarks>
    public static string StatusLine(StagedImport run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Current is not { } step)
        {
            return string.Empty;
        }

        string name = StepName(step.Step.Kind);
        string line = step.Progress is { Total: > 0 } progress
            ? $"{name}: {ProgressText(progress)}"
            : $"{name}…";

        return run.CancelRequested ? line + ". Cancelling at the next step or chunk." : line;
    }
}
