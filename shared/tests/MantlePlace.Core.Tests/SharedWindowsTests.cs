using System.Text.RegularExpressions;

namespace MantlePlace.Core.Tests;

internal static class SharedWindowsTests
{
    internal const string SharedWindowsFolder = "shared/src/MantlePlace.Wpf";

    private static readonly string[] HostTrees = ["revit/src", "autocad/src"];

    private static readonly string[] SharedTypes =
    [
        "SignInWindow",
        "VaultBrowserWindow",
        "AboutWindow",
        "NoticePopup",
        "BrandChrome",
        "WindowStyles",
        "ImportWindowThread",
        "ImportWindowRequest",
        "IImportWindowHost",
        "ForegroundWatch",
        "HostWindowGeometry",
    ];

    private static readonly string[] RetiredHostTypes = ["ImportWindowHost", "RevitWindowGeometry"];

    private static readonly string[] HostIds = ["revit", "autocad"];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("each host is named in its windows by its own product name", () =>
        {
            run.Equal(WindowLabels.HostName("revit"), "Revit", "revit");
            run.Equal(WindowLabels.HostName("autocad"), "AutoCAD", "autocad");
        });

        run.Case("a host the windows do not know is named by its id, not refused", () =>
        {
            run.Equal(WindowLabels.HostName("rhino"), "rhino", "an id with no product name");
        });

        run.Case("the vault's buttons, in order, for a host that prepares and imports from the vault (Revit)", () =>
        {
            string[] faces = [.. WindowLabels.VaultButtons(imports: true, WindowLabels.HostName("revit")).Select(button => button.Face)];
            string[] expected =
            [
                WindowLabels.Refresh,
                WindowLabels.PrepareForRevit,
                WindowLabels.Import,
                WindowLabels.RemoveDownload,
                WindowLabels.ReportProblem,
                WindowLabels.Cancel,
            ];

            run.Equal(string.Join(" | ", faces), string.Join(" | ", expected), "Revit's vault buttons");
        });

        run.Case("the vault's buttons, in order, for a host with no import from the vault", () =>
        {
            string[] faces = [.. WindowLabels.VaultButtons(imports: false, WindowLabels.HostName("autocad")).Select(button => button.Face)];
            string[] expected =
            [
                WindowLabels.Refresh,
                WindowLabels.RemoveDownload,
                WindowLabels.ReportProblem,
                WindowLabels.Cancel,
            ];

            run.Equal(string.Join(" | ", faces), string.Join(" | ", expected), "a host with no import's vault buttons");
        });

        run.Case("the vault's buttons, in order, for AutoCAD, which prepares and imports from the vault", () =>
        {
            string[] faces = [.. WindowLabels.VaultButtons(imports: true, WindowLabels.HostName("autocad")).Select(button => button.Face)];
            string[] expected =
            [
                WindowLabels.Refresh,
                WindowLabels.PrepareFor("AutoCAD"),
                WindowLabels.Import,
                WindowLabels.RemoveDownload,
                WindowLabels.ReportProblem,
                WindowLabels.Cancel,
            ];

            run.Equal(string.Join(" | ", faces), string.Join(" | ", expected), "AutoCAD's vault buttons");
        });

        run.Case("Prepare names the host it prepares for, and Revit's face is the one it always was", () =>
        {
            run.Equal(WindowLabels.PrepareFor("Revit"), WindowLabels.PrepareForRevit, "Revit");
            run.Equal(WindowLabels.PrepareFor("AutoCAD"), "Prepare for AutoCAD", "AutoCAD");
        });

        run.Case("Import is the vault's one primary action, and only a host that imports from the vault shows it", () =>
        {
            VaultButton[] revit = [.. WindowLabels.VaultButtons(imports: true, "Revit")];
            run.Equal(revit.Count(button => button.Primary), 1, "one primary button");
            run.Equal(revit.Single(button => button.Primary).Action, VaultAction.Import, "the primary is Import");

            VaultButton[] autocad = [.. WindowLabels.VaultButtons(imports: false, "AutoCAD")];
            run.Equal(autocad.Count(button => button.Primary), 0, "no primary without an import");
            run.False(autocad.Any(button => button.Action is VaultAction.Prepare or VaultAction.Import), "no Prepare or Import");
        });

        run.Case("a signed-out vault sends the curator to the host's own Mantle Place tab, never the Add-ins tab", () =>
        {
            run.Equal(WindowLabels.SignInFirst("revit"), $"Sign in first: Mantle Place ▸ Account ▸ {AccountRibbon.SignInFace}.", "Revit");
            run.Equal(WindowLabels.SignInFirst("autocad"), $"Sign in first: Mantle Place ▸ {AccountRibbon.SignInFace}.", "AutoCAD");

            foreach (string host in HostIds)
            {
                string line = WindowLabels.SignInFirst(host);
                run.False(line.Contains("Add-ins", StringComparison.OrdinalIgnoreCase), $"{host}: no Add-ins tab");
                run.True(line.StartsWith("Sign in first: Mantle Place ▸ ", StringComparison.Ordinal), $"{host}: starts at the Mantle Place tab");
                run.True(line.EndsWith($"{AccountRibbon.SignInFace}.", StringComparison.Ordinal), $"{host}: ends on the ribbon's Sign In");
            }
        });

        run.Case("the About window's heading names the host it is running in", () =>
        {
            run.Equal(AboutPage.Heading(WindowLabels.HostName("revit")), "Mantle Place for Revit", "Revit");
            run.Equal(AboutPage.Heading(WindowLabels.HostName("autocad")), "Mantle Place for AutoCAD", "AutoCAD");
        });

        run.Case("the shared windows' names are the standard's words for the shared actions (HPS-51)", () =>
        {
            HashSet<string>? words = StandardWords();
            run.True(words is not null, "the standard's HPS-51 table is found");
            if (words is null)
            {
                return;
            }

            foreach (string word in new[]
            {
                WindowLabels.SignInHeading,
                WindowLabels.VaultHeading,
                AboutPage.Title,
                WindowLabels.NoticesHeading,
                WindowLabels.ReportProblem,
            })
            {
                run.True(words.Contains(word), $"\"{word}\" is one of HPS-51's words");
            }
        });

        run.Case("the notice's heading names the Notices surface, under the product, as the other titles do", () =>
        {
            run.Equal(WindowLabels.NoticesHeading, "Notices", "the surface's word (HPS-62)");
            run.Equal(WindowLabels.NoticeTitle, "Mantle Place Notices", "the notice's heading");
        });

        run.Case("each shared window exists once, in the shared home, and neither host tree holds a copy", () =>
        {
            string? shared = RepoTree.Find(SharedWindowsFolder);
            run.True(shared is not null, SharedWindowsFolder);
            if (shared is null)
            {
                return;
            }

            foreach (string type in SharedTypes)
            {
                Regex declared = Declaration(type);

                int inShared = Directory.GetFiles(shared, "*.cs", SearchOption.AllDirectories)
                    .Where(file => !IsBuildOutput(file))
                    .Count(file => declared.IsMatch(File.ReadAllText(file)));
                run.Equal(inShared, 1, $"{type} is declared once in {SharedWindowsFolder}");

                foreach (string tree in HostTrees)
                {
                    string? folder = RepoTree.Find(tree);
                    run.True(folder is not null, tree);
                    if (folder is null)
                    {
                        continue;
                    }

                    foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file)))
                    {
                        run.False(declared.IsMatch(File.ReadAllText(file)), $"{tree} declares no {type} ({Path.GetFileName(file)})");
                    }
                }
            }
        });

        run.Case("the import window is declared in the shared home only, and no host keeps its old names", () =>
        {
            string? shared = RepoTree.Find(SharedWindowsFolder);
            run.True(shared is not null, SharedWindowsFolder);
            if (shared is null)
            {
                return;
            }

            Regex window = Declaration("ImportWindow");
            run.True(
                Directory.GetFiles(shared, "*.cs", SearchOption.AllDirectories)
                    .Where(file => !IsBuildOutput(file))
                    .Any(file => window.IsMatch(File.ReadAllText(file))),
                $"ImportWindow is declared in {SharedWindowsFolder}");

            foreach (string tree in HostTrees)
            {
                string? folder = RepoTree.Find(tree);
                if (folder is null)
                {
                    run.Fail(tree);
                    continue;
                }

                foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file)))
                {
                    string text = File.ReadAllText(file);
                    foreach (string type in RetiredHostTypes.Append("ImportWindow"))
                    {
                        run.False(Declaration(type).IsMatch(text), $"{tree} declares no {type} ({Path.GetFileName(file)})");
                    }
                }
            }
        });

        run.Case("the level row draws a locked Extra as the control's words and nothing to press", () =>
        {
            string? shared = RepoTree.Find(SharedWindowsFolder);
            string row = shared is null ? string.Empty : File.ReadAllText(Path.Combine(shared, "LevelChoiceRow.cs"));
            Match drawn = Regex.Match(
                row,
                @"foreach\s*\(\s*string\s+(?<r>\w+)\s+in\s+control\.LockedRows\s*\)\s*\{(?<body>.*?)\r?\n            \}",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            string body = drawn.Groups["body"].Value;
            Match ctor = Regex.Match(row, @"public\s+LevelChoiceRow\s*\(\s*\)\s*\{(?<body>.*?)?
    \}", RegexOptions.CultureInvariant | RegexOptions.Singleline);
            run.True(ctor.Success, "the row has its constructor");
            run.True(
                Regex.IsMatch(ctor.Groups["body"].Value, @"^\s*Children\.Add\(\s*_locked\s*\)\s*;", RegexOptions.CultureInvariant | RegexOptions.Multiline),
                "the locked panel is attached to the row in the constructor, so what is added to it is drawn");
            run.True(drawn.Success, "it loops over the rows the control decided");
            run.True(
                Regex.IsMatch(body, @"^\s*_locked\.Children\.Add\(\s*new\s+TextBlock\b", RegexOptions.CultureInvariant),
                "and adds a TextBlock to the locked panel for each, as the first thing it does");
            run.True(
                Regex.IsMatch(body, $@"Text\s*=\s*{Regex.Escape(drawn.Groups["r"].Value)}\s*,", RegexOptions.CultureInvariant),
                "showing the row's words");
            run.True(body.Contains("IsEnabled = false", StringComparison.Ordinal), "disabled, so there is nothing to press");
            run.True(!row.Contains("Button", StringComparison.Ordinal), "and offers no button, so no request for a locked Extra");
        });

        return run.Report("shared windows");
    }

    private static Regex Declaration(string type)
        => new($@"\b(class|record|struct|interface)\s+{type}\b", RegexOptions.CultureInvariant);

    private static bool IsBuildOutput(string file)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal);
    }

    private static HashSet<string>? StandardWords()
    {
        string? docs = RepoTree.Find("docs", folder => File.Exists(Path.Combine(folder, "host-plugin-standard.md")));
        string? path = docs is null ? null : Path.Combine(docs, "host-plugin-standard.md");
        if (path is null)
        {
            return null;
        }

        string[] lines = File.ReadAllLines(path);
        int start = Array.FindIndex(lines, line => line.StartsWith("| The action ", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        Regex ticked = new("`([^`]+)`", RegexOptions.CultureInvariant);
        for (int index = start + 2; index < lines.Length && lines[index].StartsWith('|'); index++)
        {
            string[] cells = lines[index].Split('|');
            if (cells.Length < 3)
            {
                continue;
            }

            foreach (Match match in ticked.Matches(cells[2]))
            {
                words.Add(match.Groups[1].Value);
            }
        }

        if (lines.Any(line => line.Contains("The words are `Credits` and `Notices`", StringComparison.Ordinal)))
        {
            words.Add("Credits");
            words.Add("Notices");
        }

        return words;
    }
}
