namespace MantlePlace.AutoCad;

/// <summary>Where F1 over a Mantle Place command in AutoCAD or Civil 3D sends a curator.</summary>
/// <remarks>
/// One constant, held in the pure core so a test can pin it; the partial CUIX file hands it to every
/// command. Without it F1 over our buttons opens AutoCAD's own help, which knows nothing of them. The
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
/// <param name="Label">The button's text, which is also the macro's name.</param>
/// <param name="Tooltip">One line the ribbon shows over the button.</param>
/// <param name="Key">A short word the file's identifiers are built from.</param>
public sealed record PanelCommand(string Command, string Label, string Tooltip, string Key)
{
    /// <summary>The macro's identifier in the CUIX file.</summary>
    public string MacroId => "ID_MANTLEPLACE_" + Key;

    /// <summary>The button's identifier in the CUIX file.</summary>
    public string ButtonId => "MANTLEPLACE_BTN_" + Key;
}

/// <summary>
/// The plugin's ribbon panel, as the partial CUIX file declares it: which commands, in which order,
/// on which of the host's tabs.
/// </summary>
/// <remarks>
/// <para>
/// A partial CUIX file is how a bundle puts a panel on the ribbon without any ribbon code in the
/// shell. The panel goes onto the host's Plug-Ins tab (the tab AutoCAD 2025 and later names Add-ins)
/// by giving the partial a tab of the same identifier, which AutoCAD merges with the main file's.
/// </para>
/// <para>
/// The commands are the shell's <c>CommandMethod</c> names. A ribbon button cannot show the session,
/// so sign-in and sign-out are two buttons where Revit's Account face is one.
/// </para>
/// </remarks>
public static class PlugInsPanel
{
    /// <summary>The identifier of the host's Plug-Ins tab in AutoCAD's main customization file.</summary>
    public const string TabId = "RBN_00012112";

    /// <summary>The text the host gives that tab.</summary>
    public const string TabText = "Add-ins";

    /// <summary>The panel's title.</summary>
    public const string PanelText = "Mantle Place";

    /// <summary>The customization group's name, which every identifier the file adds sits under.</summary>
    public const string GroupName = "MANTLEPLACE";

    /// <summary>The partial file's name inside the bundle's <c>Contents</c> folder.</summary>
    public const string FileName = "MantlePlace.cuix";

    /// <summary>The panel's commands, left to right.</summary>
    public static IReadOnlyList<PanelCommand> Commands { get; } =
    [
        new("MANTLEPLACESIGNIN", "Sign In", "Sign in to your Mantle Place account in your browser.", "SIGNIN"),
        new("MANTLEPLACESIGNOUT", "Sign Out", "Sign out and clear the session stored on this machine.", "SIGNOUT"),
        new("MANTLEPLACEVAULT", "Vault", "Browse and search the bundles in your Mantle Place vault.", "VAULT"),
        new("MANTLEPLACEFEEDBACK", "Send Feedback", "Open the Mantle Place feedback form in your browser.", "FEEDBACK"),
        new("MANTLEPLACEABOUT", "About", "Show which Mantle Place build is loaded and whether you are signed in.", "ABOUT"),
    ];

    /// <summary>
    /// The monogram renders the buttons use, small then large. They are the Revit plugin's own files
    /// (the one logo the plugins show), copied in by the package script, never stored a second time.
    /// </summary>
    public static IReadOnlyList<string> ImageSources { get; } = ["MantlePlaceMark_16.png", "MantlePlaceMark_32.png"];
}
