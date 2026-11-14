using System.Text.RegularExpressions;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Two faults a storefront review found in the Revit shim, read off the shim's own source, because CI
/// never builds the shim and no other check here would have seen either.
/// </summary>
/// <remarks>
/// <para>
/// <b>The title bar.</b> A WPF window given no icon takes the host process's, so every window the
/// plugin opened carried the host's logo in its title bar. Each one now sets the Mantle Place mark
/// through <c>BrandChrome.ShowMarkInTitleBar</c>; a window with no title bar at all
/// (<c>WindowStyle.None</c>, the notices) has nowhere to show it.
/// </para>
/// <para>
/// <b>A task dialog's default command link.</b> Revit checks <c>TaskDialog.DefaultButton</c> when it
/// is set, and a command link that has not been added yet is refused with "Corresponding button not
/// found. Parameter name: defaultButton". Set in the object initializer, it ran before every
/// <c>AddCommandLink</c>, so Open in Curator stopped before its size dialog ever showed.
/// </para>
/// <para>
/// Compiled into the AutoCAD suite too, which runs it over its own shim.
/// </para>
/// </remarks>
internal static class ShimWindowTests
{
    /// <summary>The call that puts the mark in a window's title bar.</summary>
    private const string MarkCall = "BrandChrome.ShowMarkInTitleBar(this)";

    private static readonly Regex WindowClass = new(@"class\s+(\w+)\s*:\s*Window\b", RegexOptions.CultureInvariant);

    private static readonly Regex InitializerDefaultLink = new(
        @"^\s*DefaultButton\s*=\s*TaskDialogResult\.CommandLink\d\s*,?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex DefaultStatement = new(@"\.DefaultButton\s*=", RegexOptions.CultureInvariant);

    /// <param name="shimRelativePath">The shim's source folder, from the repository root.</param>
    /// <param name="label">The suite's name for it in the report.</param>
    internal static int Run(string shimRelativePath, string label)
    {
        TestRun run = new();

        string? folder = RepoTree.Find(shimRelativePath);

        run.Case("the shim's source is found", () => run.True(folder is not null, shimRelativePath));
        if (folder is null)
        {
            return run.Report(label);
        }

        string[] files = Directory.GetFiles(folder, "*.cs", SearchOption.TopDirectoryOnly);

        run.Case("every window with a title bar shows the Mantle Place mark there, not the host's", () =>
        {
            int windows = 0;
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                foreach (Match match in WindowClass.Matches(text))
                {
                    windows++;
                    bool titled = !text.Contains("WindowStyle = WindowStyle.None", StringComparison.Ordinal);
                    run.True(
                        !titled || text.Contains(MarkCall, StringComparison.Ordinal),
                        $"{match.Groups[1].Value} ({Path.GetFileName(file)}) calls {MarkCall}");
                }

                run.False(
                    text.Contains("new Window(", StringComparison.Ordinal) || text.Contains("new Window\n", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} builds no bare Window, which would carry the host's logo");
            }

            run.True(windows > 0, "the shim's windows were found at all");
        });

        run.Case("a task dialog names a command link its default only once the link is added", () =>
        {
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                int firstLink = text.IndexOf("AddCommandLink(", StringComparison.Ordinal);
                if (firstLink < 0)
                {
                    continue;
                }

                string name = Path.GetFileName(file);
                run.False(
                    InitializerDefaultLink.IsMatch(text),
                    $"{name} sets no command link as DefaultButton in an initializer, which runs before AddCommandLink");

                foreach (Match statement in DefaultStatement.Matches(text))
                {
                    run.True(
                        statement.Index > firstLink,
                        $"{name} sets DefaultButton after its first AddCommandLink (offset {statement.Index})");
                }
            }
        });

        return run.Report(label);
    }
}
