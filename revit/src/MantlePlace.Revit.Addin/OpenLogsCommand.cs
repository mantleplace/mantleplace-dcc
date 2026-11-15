using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>"Logs": shows the most recent import or probe log in File Explorer.</summary>
/// <remarks>
/// <para>
/// The only reason this is a button is that the logs are not where a curator would look. Both
/// writers put their file beside the zip they were handed, and a zip that came through the vault
/// lives under <c>%LOCALAPPDATA%\MantlePlace\bundles\&lt;order&gt;\</c> — a path nothing in the UI
/// has ever shown them. Asking them to paste it out of a support reply is the problem this replaces.
/// </para>
/// <para>
/// Every decision is <see cref="OpenLogsTarget"/>'s, the directory walk is
/// <see cref="BundleLogSearch"/>'s and the shell call is <see cref="ShellLauncher"/>'s, all three of
/// which CI builds and runs. What is left here is the dialog, which needs Revit.
/// </para>
/// <para>
/// It touches no <see cref="Document"/> and opens no transaction, so it is reachable with no project
/// open — which matters, because "the import was refused and I do not know why" is a state a curator
/// can be in after closing the model.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenLogsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Show();
        return Result.Succeeded;
    }

    /// <summary>
    /// Shows the newest log, or says why there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Shared with the About window's Logs button on purpose.</b> Two commands that both claim
    /// to open the logs and open different folders is a support call that starts with the curator
    /// and the maintainer looking at different directories. One function, one answer — see
    /// <see cref="AboutMantlePlace"/>.
    /// </para>
    /// <para>
    /// The About window is the shared WPF one, and its button runs on Revit's thread but outside a
    /// Revit API context, where a <c>TaskDialog</c> is not allowed; from there the two sentences
    /// below are a WPF message box, as <see cref="ClientUpdateDialog"/>'s are.
    /// </para>
    /// </remarks>
    /// <param name="fromWindow">True when called from a window's button rather than a ribbon command.</param>
    internal static void Show(bool fromWindow = false)
    {
        OpenLogsTarget target = BundleLogSearch.ResolveTarget();

        if (target.Notice is { } notice)
        {
            Say("No log has been written yet.", notice, fromWindow);
        }

        bool opened = target.RevealFilePath is { } log
            ? ShellLauncher.TryReveal(log, target.FolderPath, out string refused)
            : ShellLauncher.TryOpenFolder(target.FolderPath, out refused);

        if (!opened)
        {
            // Not worth failing the command over — the path is the answer, and a curator can paste
            // it somewhere themselves.
            Say("Could not open File Explorer.", $"Open it yourself: {refused}", fromWindow);
        }
    }

    private static void Say(string instruction, string content, bool fromWindow)
    {
        if (fromWindow)
        {
            _ = System.Windows.MessageBox.Show(
                $"{instruction}\n\n{content}", "Mantle Place", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        new TaskDialog("Mantle Place")
        {
            MainInstruction = instruction,
            MainContent = content,
        }.Show();
    }
}
