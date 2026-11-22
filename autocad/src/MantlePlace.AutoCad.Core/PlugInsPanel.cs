namespace MantlePlace.AutoCad;

public static class AutoCadHelp
{
    public const string PageUrl = "https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/README.md";
}

public sealed record PanelCommand(string Command, string Label, string Tooltip, string Key)
{
    public string ButtonId => "MANTLEPLACE_BTN_" + Key;

    public string HelpTopic => Key.ToLowerInvariant();
}

public sealed record RibbonTabSeen(string Id, IReadOnlyList<string> PanelIds);

public sealed record PanelPlacement(string TabId, bool MakeTab);

public static class PlugInsPanel
{
    public const string PanelId = "MANTLEPLACE_PANEL";

    public const string OwnTabId = "MANTLEPLACE_TAB";

    public const string TabText = "Mantle Place";

    public const string PanelText = "Mantle Place";

    public static IReadOnlyList<PanelCommand> Commands { get; } =
    [
        new("MANTLEPLACESIGNIN", "Sign In", "Sign in to your Mantle Place account in your browser.", "SIGNIN"),
        new("MANTLEPLACESIGNOUT", "Sign Out", "Sign out and clear the session stored on this machine.", "SIGNOUT"),
        new("MANTLEPLACEVAULT", "Vault", "Browse and search the bundles in your Mantle Place vault.", "VAULT"),
        new("MANTLEPLACEIMPORTBUNDLE", "Import Bundle", "Import a Mantle Place bundle zip from this computer into the active drawing. No account needed.", "IMPORTBUNDLE"),
        new("MANTLEPLACEABOUT", "About Mantle Place", "Show which Mantle Place build is loaded and whether you are signed in.", "ABOUT"),
        new("MANTLEPLACEFEEDBACK", "Send Feedback", "Open the Mantle Place feedback form in your browser.", "FEEDBACK"),
    ];

    public static IReadOnlyList<string> ImageSources { get; } = ["MantlePlaceMark_16.png", "MantlePlaceMark_32.png"];

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
