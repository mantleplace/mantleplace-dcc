namespace MantlePlace.Core;

public enum VaultAction
{
    Refresh,

    Prepare,

    Import,

    RemoveDownload,

    ReportProblem,

    Cancel,
}

public sealed record VaultButton(VaultAction Action, string Face, bool Primary);

public static partial class WindowLabels
{
    public const string NoticesHeading = "Notices";

    public const string NoticeTitle = "Mantle Place " + NoticesHeading;

    public static string HostName(string hostId) => hostId switch
    {
        "revit" => "Revit",
        "autocad" => "AutoCAD",
        _ => hostId,
    };

    public static string PrepareFor(string hostName) => $"Prepare for {hostName}";

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

    public static string SignInFirst(string hostId) => hostId switch
    {
        "revit" => $"Sign in first: Mantle Place ▸ Account ▸ {AccountRibbon.SignInFace}.",
        _ => $"Sign in first: Mantle Place ▸ {AccountRibbon.SignInFace}.",
    };
}
