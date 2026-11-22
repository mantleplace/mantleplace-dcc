using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

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
