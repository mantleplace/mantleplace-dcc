using System.Text.RegularExpressions;

namespace MantlePlace.Core.Tests;

internal static class ShimWindowTests
{
    private const string MarkCall = "BrandChrome.ShowMarkInTitleBar(this)";

    private static readonly Regex WindowClass = new(@"class\s+(\w+)\s*:\s*Window\b", RegexOptions.CultureInvariant);

    private static readonly Regex InitializerDefaultLink = new(
        @"^\s*DefaultButton\s*=\s*TaskDialogResult\.CommandLink\d\s*,?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex DefaultStatement = new(@"\.DefaultButton\s*=", RegexOptions.CultureInvariant);

    internal static int Run(string shimRelativePath, string label, bool declaresWindows = true)
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

            if (declaresWindows)
            {
                run.True(windows > 0, "the shim's windows were found at all");
            }
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
