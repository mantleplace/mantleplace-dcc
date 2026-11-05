namespace MantlePlace.AutoCad;

/// <summary>Where F1 over a Mantle Place command in AutoCAD or Civil 3D sends a curator.</summary>
/// <remarks>
/// One constant, held in the pure core so a test can pin it; the shell hands it to every button's
/// tooltip. Without it F1 over our buttons opens AutoCAD's own help, which knows nothing of them. The
/// page is the AutoCAD plugin's README in the public repository, which lists every command and is
/// versioned with the code the command belongs to.
/// </remarks>
public static class AutoCadHelp
{
    /// <summary>The AutoCAD plugin's public help page.</summary>
    public const string PageUrl = "https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/README.md";
}

/// <summary>One command on the plugin's ribbon panel.</summary>
/// <param name="Command">The registered command name, which is what the button types.</param>
/// <param name="Label">The button's text.</param>
/// <param name="Tooltip">One line the ribbon shows over the button.</param>
/// <param name="Key">A short word the button's identifier and help topic are built from.</param>
public sealed record PanelCommand(string Command, string Label, string Tooltip, string Key)
{
    /// <summary>The button's identifier on the ribbon.</summary>
    public string ButtonId => "MANTLEPLACE_BTN_" + Key;

    /// <summary>The topic F1 over the button hands the help page, beside <see cref="AutoCadHelp.PageUrl"/>.</summary>
    public string HelpTopic => Key.ToLowerInvariant();
}

/// <summary>One tab the shell found on the host's ribbon.</summary>
/// <param name="Id">The tab's identifier; the host builds a customization-file tab's as <c>MENUGROUP.UID</c>.</param>
/// <param name="PanelIds">The identifiers of the panels on it.</param>
public sealed record RibbonTabSeen(string Id, IReadOnlyList<string> PanelIds);

/// <summary>Where the panel goes.</summary>
/// <param name="TabId">The tab's identifier.</param>
/// <param name="MakeTab">True when the shell must make the tab first; false when it is already on the ribbon.</param>
public sealed record PanelPlacement(string TabId, bool MakeTab);

/// <summary>
/// The plugin's ribbon panel: which commands, in which order, and which of the ribbon's tabs it goes on.
/// </summary>
/// <remarks>
/// <para>
/// The shell builds the panel in code and puts it on a <c>Mantle Place</c> tab of the plugin's own, as
/// Revit's ribbon does (<c>HPS-57</c>), so a curator moving between hosts looks in one place. It was
/// the host's Add-ins tab once (the tab AutoCAD named Plug-Ins before 2025); and before that a partial
/// CUIX file, which the host cannot merge: a tab in a partial file is its own customization group's,
/// so a tab given the Add-ins tab's UID came up as <c>MANTLEPLACE.RBN_00012112</c>, a second
/// "Add-ins" tab beside the host's, in Civil 3D 2027 and in AutoCAD 2027 alike
/// (<c>autocad/docs/api-record.md</c>).
/// </para>
/// <para>
/// The tab is found by its identifier, never its title, which is translated. The Autodesk storefront's
/// guidance has pointed apps at the host's Add-ins tab, so a reviewer may ask for it back; the whole
/// of that change is the one choice in <see cref="Place"/>, which would name the host's tab
/// (<c>CIVIL.ID_Civil3DAddins</c> in Civil 3D, <c>ACAD.RBN_00012112</c> in AutoCAD) in place of
/// <see cref="OwnTabId"/>.
/// </para>
/// <para>
/// The commands are the shell's <c>CommandMethod</c> names, in <c>HPS-51</c>'s order. A ribbon button
/// cannot show the session, so sign-in and sign-out are two buttons where Revit's Account face is one.
/// </para>
/// </remarks>
public static class PlugInsPanel
{
    /// <summary>The panel's identifier on the ribbon, which is how the shell knows it is already there.</summary>
    public const string PanelId = "MANTLEPLACE_PANEL";

    /// <summary>The identifier of the plugin's own tab.</summary>
    public const string OwnTabId = "MANTLEPLACE_TAB";

    /// <summary>The title of the plugin's own tab (<c>HPS-57</c>).</summary>
    public const string TabText = "Mantle Place";

    /// <summary>The panel's title.</summary>
    public const string PanelText = "Mantle Place";

    /// <summary>The panel's commands, left to right, in the order <c>HPS-51</c>'s table lists the actions.</summary>
    public static IReadOnlyList<PanelCommand> Commands { get; } =
    [
        new("MANTLEPLACESIGNIN", "Sign In", "Sign in to your Mantle Place account in your browser.", "SIGNIN"),
        new("MANTLEPLACESIGNOUT", "Sign Out", "Sign out and clear the session stored on this machine.", "SIGNOUT"),
        new("MANTLEPLACEVAULT", "Vault", "Browse and search the bundles in your Mantle Place vault.", "VAULT"),
        new("MANTLEPLACEABOUT", "About", "Show which Mantle Place build is loaded and whether you are signed in.", "ABOUT"),
        new("MANTLEPLACEFEEDBACK", "Send Feedback", "Open the Mantle Place feedback form in your browser.", "FEEDBACK"),
    ];

    /// <summary>
    /// The monogram renders the buttons use, small then large. They are the Revit plugin's own files
    /// (the one logo the plugins show), embedded in the shell, never stored a second time.
    /// </summary>
    public static IReadOnlyList<string> ImageSources { get; } = ["MantlePlaceMark_16.png", "MantlePlaceMark_32.png"];

    /// <summary>Where the panel goes on a ribbon holding <paramref name="tabs"/>, or null when it is already on one.</summary>
    /// <remarks>
    /// The host rebuilds its ribbon from its customization files on a workspace change, which drops a
    /// panel added in code, so the shell asks again each time; null is what makes that safe to repeat.
    /// The plugin's tab is made only when it is not on the ribbon, so a rebuild that kept the tab and
    /// dropped the panel puts the panel back and never adds a second tab.
    /// </remarks>
    public static PanelPlacement? Place(IReadOnlyList<RibbonTabSeen> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        if (tabs.Any(tab => tab.PanelIds.Contains(PanelId, StringComparer.Ordinal)))
        {
            return null;
        }

        bool ownTabIsThere = tabs.Any(tab => string.Equals(tab.Id, OwnTabId, StringComparison.Ordinal));
        return new PanelPlacement(OwnTabId, MakeTab: !ownTabIsThere);
    }
}
