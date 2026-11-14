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

        run.Case("Import Bundle is a registered command, and the panel's button types it", () =>
        {
            run.Contains(commands, $"[CommandMethod(\"{LocalBundleImport.Command}\")]", "registered");
            run.True(
                PlugInsPanel.Commands.Any(c => c.Command == LocalBundleImport.Command && c.Label == "Import Bundle"),
                "the panel's Import Bundle button (HPS-51's words)");
        });

        run.Case("the command and a received export reach the same importer", () =>
        {
            string body = MethodBody(commands, LocalBundleImport.Command);
            run.Contains(body, "OrderImporter.Import(", "Import Bundle places through the importer");
            run.Contains(received, "OrderImporter.Import(", "so does the received export's Import button");
            run.Equal(Regex.Matches(commands + received, @"OrderImporter\.Import\(").Count, 2, "and nothing else places an order");
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
