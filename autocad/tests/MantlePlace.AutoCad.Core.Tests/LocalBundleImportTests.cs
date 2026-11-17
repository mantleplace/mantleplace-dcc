using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MantlePlace.Client;
using MantlePlace.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// Import Bundle (<c>HPS-61</c>): a bundle zip already on disk, imported into the active drawing with no
/// send from the web vault. What a file must be before the importer reads it is decided in the core and
/// pinned here; that the command and a received export reach the one importer is read off the shell's
/// own source, because CI never builds the shell.
/// </summary>
internal static class LocalBundleImportTests
{
    private const string Shell = "autocad/src/MantlePlace.AutoCad.Addin";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mantleplace-local-import-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            Opening(run, sandbox);
            SameImporter(run);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return run.Report("Import Bundle");
    }

    private static void Opening(TestRun run, string sandbox)
    {
        run.Case("a file that is not a zip is refused as not a bundle, and nothing is opened", () =>
        {
            string path = Path.Combine(sandbox, "notes.zip");
            File.WriteAllText(path, "these are notes, not a bundle");

            bool opened = LocalBundleImport.TryOpen(path, out LocalBundleArchive? archive, out string? refusal);
            archive?.Dispose();

            run.False(opened, "refused");
            run.True(archive is null, "no archive");
            run.Contains(refusal, "not a Mantle Place bundle", "says what is wrong");
            run.Contains(refusal, "Nothing was imported", "and that nothing went in");
        });

        run.Case("a zip with no manifest is refused in the importer's own words", () =>
        {
            string path = Zip(sandbox, "photos.zip", ("photo.jpg", "jpeg bytes"));

            bool opened = LocalBundleImport.TryOpen(path, out LocalBundleArchive? archive, out string? refusal);
            archive?.Dispose();

            run.False(opened, "refused");
            run.Equal(
                refusal,
                "This file is not a Mantle Place bundle: it holds no Metadata/manifest.json. Nothing was imported.",
                "the sentence a received export without a manifest gets");
        });

        run.Case("a path with no file is refused, naming the path", () =>
        {
            string path = Path.Combine(sandbox, "gone.zip");

            run.False(LocalBundleImport.TryOpen(path, out _, out string? refusal), "refused");
            run.Contains(refusal, path, "names where it looked");
            run.Contains(refusal, "Nothing was imported", "and that nothing went in");
        });

        run.Case("no path at all is refused, not thrown", () =>
        {
            run.False(LocalBundleImport.TryOpen(null, out _, out string? none), "null");
            run.Contains(none, "Nothing was imported", "a sentence");
            run.False(LocalBundleImport.TryOpen("   ", out _, out string? blank), "blank");
            run.Contains(blank, "Nothing was imported", "a sentence");
        });

        run.Case("a refusal made before any order is read names no blank order", () =>
        {
            string summary = new AutoCadImportPlan { Refusal = LocalBundleImport.NotABundle }.Summary();

            run.Equal(summary, "Mantle Place: " + LocalBundleImport.NotABundle, "no 'order :' with nothing between");
        });

        run.Case("a bundle opens with its manifest, under the path typed at the command line, quotes and all", () =>
        {
            string path = Zip(sandbox, "order.zip", ("Order/Metadata/manifest.json", "{\"manifest_version\":\"2.2.0\"}"));

            bool opened = LocalBundleImport.TryOpen($" \"{path}\" ", out LocalBundleArchive? archive, out string? refusal);
            using (archive)
            {
                run.True(opened, "opened");
                run.True(refusal is null, "no refusal");
                run.Contains(archive?.ManifestText, "manifest_version", "the manifest the planner reads");
            }
        });
    }

    private static void SameImporter(TestRun run)
    {
        string? folder = RepoTree.Find(Shell);
        run.Case("the shell's source is found", () => run.True(folder is not null, Shell));
        if (folder is null)
        {
            return;
        }

        string commands = File.ReadAllText(Path.Combine(folder, "PluginCommands.cs"));
        string received = File.ReadAllText(Path.Combine(folder, "AutoCadExportImportHost.cs"));
        string importer = File.ReadAllText(Path.Combine(folder, "OrderImporter.cs"));
        string windowed = File.ReadAllText(Path.Combine(folder, "WindowedImport.cs"));

        run.Case("Import Bundle is a registered command, and the panel's button types it", () =>
        {
            run.Contains(commands, $"[CommandMethod(\"{LocalBundleImport.Command}\")]", "registered");
            run.True(
                PlugInsPanel.Commands.Any(c => c.Command == LocalBundleImport.Command && c.Label == "Import Bundle"),
                "the panel's Import Bundle button (HPS-51's words)");
        });

        run.Case("the command and a received export reach the same importer, through the shared import window (HPS-60)", () =>
        {
            string body = MethodBody(commands, LocalBundleImport.Command);
            run.Contains(body, "WindowedImport.Start(", "Import Bundle imports in the import window");
            run.Contains(received, "WindowedImport.Start(", "so does a received export");
            run.Equal(Regex.Matches(commands + received, @"WindowedImport\.Start\(").Count, 2, "and nothing else starts an import");
            run.Contains(windowed, "ImportWindowThread.Open(", "the shared window, not one of this host's own");
            run.Contains(windowed, "OrderImporter.Prepare(", "which places through the one importer");
            run.Equal(Regex.Matches(commands + received + windowed, @"OrderImporter\.Prepare\(").Count, 1, "and nothing else opens an order");
        });

        run.Case("an unattended run names its zip in the shared variable, as Revit's does, and the prompt is skipped", () =>
        {
            string body = MethodBody(commands, LocalBundleImport.Command);
            run.Contains(body, "LocalBundleSource.Unattended(Environment.GetEnvironmentVariable(LocalBundleSource.PathVariable))", "the one variable, read through the shared rule");
            run.True(
                body.IndexOf("LocalBundleSource.Unattended(", StringComparison.Ordinal) < body.IndexOf("GetFileNameForOpen(", StringComparison.Ordinal),
                "read before AutoCAD's file prompt, which it skips");
        });

        run.Case("a refused start still writes the record beside the zip, so a script waiting for it hears why", () =>
        {
            string body = MethodBody(commands, LocalBundleImport.Command);
            run.True(
                Regex.IsMatch(body, @"if \(!WindowedImport\.Start\([^\n]*\)\)\s*\{[^}]*WriteRecord\(zipPath, "),
                "the branch taken when no import starts writes the record, with the reason");
        });

        run.Case("the received-export window is gone: a send opens the shared import window", () =>
        {
            run.False(File.Exists(Path.Combine(folder, "ReceivedExportWindow.cs")), "no window of this host's own");
            run.False(received.Contains("ReceivedExportWindow", StringComparison.Ordinal), "and the connected host names none");
        });

        run.Case("the importer opens every bundle through the core's check, so both get the same refusal", () =>
        {
            run.Contains(importer, "LocalBundleImport.TryOpen(", "the one opening");
            run.False(importer.Contains("LocalBundleArchive.Open(", StringComparison.Ordinal), "no second, unchecked opening");
        });

        run.Case("Import Bundle needs no account: it reaches no session and no connected host (HPS-61)", () =>
        {
            string body = MethodBody(commands, LocalBundleImport.Command);
            run.False(body.Contains("Session", StringComparison.Ordinal), "no session");
            run.False(body.Contains("Connected", StringComparison.Ordinal), "no connected host");
        });
    }

    /// <summary>The text from a command's attribute to the next command's, or to the end.</summary>
    private static string MethodBody(string source, string command)
    {
        int start = source.IndexOf($"[CommandMethod(\"{command}\")]", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        int next = source.IndexOf("[CommandMethod(", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Zip(string sandbox, string name, params (string Entry, string Text)[] entries)
    {
        string path = Path.Combine(sandbox, name);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entry, string text) in entries)
        {
            using Stream stream = zip.CreateEntry(entry).Open();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }
}
