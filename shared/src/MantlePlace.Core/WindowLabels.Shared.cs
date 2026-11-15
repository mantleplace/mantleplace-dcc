namespace MantlePlace.Core;

/// <summary>What a button in the vault window does. The window wires each to its handler.</summary>
public enum VaultAction
{
    /// <summary>Re-list the vault.</summary>
    Refresh,

    /// <summary>Materialize this host's deliverables, then download them.</summary>
    Prepare,

    /// <summary>Import the selected bundle, through the host's own import.</summary>
    Import,

    /// <summary>Evict the selected order from the bundle cache.</summary>
    RemoveDownload,

    /// <summary>Open the web Feedback form for the selected bundle.</summary>
    ReportProblem,

    /// <summary>Stop the step in flight.</summary>
    Cancel,
}

/// <summary>One button in the vault window's row: what it does, what it says, and whether it is the primary action.</summary>
/// <param name="Action">What pressing it does.</param>
/// <param name="Face">The words on it.</param>
/// <param name="Primary">Whether it is drawn in the brand colour, as the one action the curator came to do.</param>
public sealed record VaultButton(VaultAction Action, string Face, bool Primary);

/// <summary>
/// The labels of the windows both .NET hosts open from one source (<c>shared/src/MantlePlace.Wpf</c>):
/// where a host's name enters them, and which buttons a host's vault window shows, in which order.
/// </summary>
/// <remarks>
/// A window hands this the host it runs in, and lays out what comes back. The host supplies only what
/// is its own: the owner window, its name where a label needs it, and its commands.
/// </remarks>
public static partial class WindowLabels
{
    /// <summary>
    /// The heading over the notices the plugin shows unasked: the surface's word (<c>HPS-62</c>), which
    /// the standard's <c>HPS-51</c> table fixes.
    /// </summary>
    public const string NoticesHeading = "Notices";

    /// <summary>
    /// The line at the head of each notice. A notice has no title bar, so this is its title, built as
    /// the other windows' are: the product, then the surface. It said <c>Mantle Place Vault</c>, which
    /// named the window a click opens rather than the notice itself.
    /// </summary>
    public const string NoticeTitle = "Mantle Place " + NoticesHeading;

    /// <summary>
    /// The host's product name, for a label or a sentence that names it, from the id the host's shell
    /// hands <see cref="HostIdentity"/>. A host the windows do not know yet is named by its id rather
    /// than refused: a plain word in a sentence is a cosmetic fault, and a throw in a window is not.
    /// </summary>
    /// <param name="hostId">The host's id: <c>revit</c>, <c>autocad</c>.</param>
    public static string HostName(string hostId) => hostId switch
    {
        "revit" => "Revit",
        "autocad" => "AutoCAD",
        _ => hostId,
    };

    /// <summary>The Prepare button's face: the host's own construct, so the host's own name (<c>HPS-51</c>).</summary>
    /// <param name="hostName">The host's product name (<see cref="HostName"/>).</param>
    public static string PrepareFor(string hostName) => $"Prepare for {hostName}";

    /// <summary>
    /// The vault window's buttons, in the order they stand in the row.
    /// </summary>
    /// <remarks>
    /// Prepare and Import are a host's hook into its document, so a host that has no import from the
    /// vault (AutoCAD, whose orders arrive as an export from the web vault or through Import Bundle)
    /// shows neither, rather than two buttons that do nothing. Import is the one primary action:
    /// Prepare is the step before it and Remove Download undoes it.
    /// </remarks>
    /// <param name="imports">Whether the host prepares and imports from the vault window.</param>
    /// <param name="hostName">The host's product name, for the Prepare face.</param>
    public static IReadOnlyList<VaultButton> VaultButtons(bool imports, string hostName)
    {
        List<VaultButton> buttons = [new(VaultAction.Refresh, Refresh, Primary: false)];
        if (imports)
        {
            buttons.Add(new(VaultAction.Prepare, PrepareFor(hostName), Primary: false));
            buttons.Add(new(VaultAction.Import, Import, Primary: true));
        }

        buttons.Add(new(VaultAction.RemoveDownload, RemoveDownload, Primary: false));
        buttons.Add(new(VaultAction.ReportProblem, ReportProblem, Primary: false));
        buttons.Add(new(VaultAction.Cancel, Cancel, Primary: false));
        return buttons;
    }

    /// <summary>
    /// What the vault window says to a signed-out curator: where Sign In is, in the host's own ribbon.
    /// </summary>
    /// <remarks>
    /// Both hosts give the plugin a tab of its own named <c>Mantle Place</c> (<c>HPS-57</c>). Revit's
    /// Sign In is the face of its Account split button; AutoCAD's tab holds one panel, named as the
    /// tab is, so the path names the tab and the button. AutoCAD's copy of the window said
    /// <c>Add-ins tab ▸ Mantle Place</c> after the panel had moved off the Add-ins tab, which is the
    /// drift one source stops.
    /// </remarks>
    /// <param name="hostId">The host's id.</param>
    public static string SignInFirst(string hostId) => hostId switch
    {
        "revit" => $"Sign in first: Mantle Place ▸ Account ▸ {AccountRibbon.SignInFace}.",
        _ => $"Sign in first: Mantle Place ▸ {AccountRibbon.SignInFace}.",
    };
}
