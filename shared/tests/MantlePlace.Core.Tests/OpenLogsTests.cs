using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class OpenLogsTests
{
    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-logs-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
        }
        finally
        {
            TryDelete(sandbox);
        }

        return run.Report("open logs");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        run.Case("a log file is recognised by either suffix and nothing else", () =>
        {
            run.True(
                LocalBundleSource.IsLogFileName("bundle.zip" + LocalBundleSource.ImportLogSuffix),
                "the import log");
            run.True(
                LocalBundleSource.IsLogFileName("bundle.zip" + LocalBundleSource.ProbeLogSuffix),
                "the probe log");

            run.False(LocalBundleSource.IsLogFileName("bundle.zip"), "the bundle itself is not a log");
            run.False(LocalBundleSource.IsLogFileName("bundle.zip.part"), "nor the partial download");
            run.False(LocalBundleSource.IsLogFileName("bundle.json"), "nor the sidecar");
            run.False(LocalBundleSource.IsLogFileName("notes.log"), "nor some other .log that landed there");
        });

        run.Case("the newest log is revealed inside its own order's folder", () =>
        {
            string order = Path.Combine(sandbox, "revealed", "order-a");
            string log = Path.Combine(order, "bundle.zip" + LocalBundleSource.ImportLogSuffix);

            OpenLogsTarget target = OpenLogsTarget.Resolve(Path.Combine(sandbox, "revealed"), log);

            run.Equal(target.FolderPath, order, "the folder Explorer opens is the log's own");
            run.Equal(target.RevealFilePath, log, "and the log itself is selected in it");
            run.True(target.Notice is null, "with nothing to say — the window is the answer");
        });

        run.Case("a cache with no log in it opens, and says why it is empty", () =>
        {
            string root = Path.Combine(sandbox, "empty-root");

            OpenLogsTarget target = OpenLogsTarget.Resolve(root, newestLogPath: null);

            run.Equal(target.FolderPath, root, "the cache root still opens");
            run.True(target.RevealFilePath is null, "with nothing to select");
            run.True(target.Notice is not null, "and a sentence saying no log is there yet");

            run.True(
                target.Notice!.Contains("beside that zip", StringComparison.Ordinal),
                "naming where a hand-picked zip logs instead");
        });

        run.Case("the search finds the newest log in any order's folder", () =>
        {
            string root = Path.Combine(sandbox, "index");
            string older = Path.Combine(root, "order-a", "bundle.zip" + LocalBundleSource.ImportLogSuffix);
            string newer = Path.Combine(root, "order-b", "bundle.zip" + LocalBundleSource.ProbeLogSuffix);

            Write(older, "an earlier run");
            Write(newer, "a later run");
            Write(Path.Combine(root, "order-b", "bundle.zip"), "not a log");

            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            Write(
                Path.Combine(root, "order-b", "extracted", "Surface", "decoy.zip" + LocalBundleSource.ImportLogSuffix),
                "not a log the search may see");

            OpenLogsTarget target = BundleLogSearch.ResolveTarget(root);

            run.Equal(target.RevealFilePath, newer, "the probe log written last wins");
            run.Equal(target.FolderPath, Path.Combine(root, "order-b"), "and its folder is what opens");
        });

        run.Case("a search over a root that does not exist explains rather than throws", () =>
        {
            string absent = Path.Combine(sandbox, "absent");

            OpenLogsTarget target = BundleLogSearch.ResolveTarget(absent);

            run.Equal(target.FolderPath, absent, "the folder to create and open");
            run.True(target.RevealFilePath is null, "with nothing to select");
            run.True(target.Notice is not null, "and a sentence saying so");
        });

        run.Case("reveal selects a log that is there, and opens its folder when the log is gone", () =>
        {
            string folder = Path.Combine(sandbox, "reveal");
            Directory.CreateDirectory(folder);
            string log = Path.Combine(folder, "bundle.zip" + LocalBundleSource.ImportLogSuffix);
            Write(log, "a log");

            List<System.Diagnostics.ProcessStartInfo> started = [];
            run.True(ShellLauncher.TryReveal(log, folder, started.Add, out _), "a present log is revealed");
            run.True(
                started.Count == 1 && started[0].FileName == "explorer.exe" && started[0].Arguments.Contains("/select", StringComparison.Ordinal) && started[0].Arguments.Contains(log, StringComparison.Ordinal),
                "by selecting it in Explorer");

            File.Delete(log);
            started.Clear();
            run.True(ShellLauncher.TryReveal(log, folder, started.Add, out _), "a log that is gone falls back");
            run.True(started.Count == 1 && started[0].FileName == folder && started[0].UseShellExecute, "to opening its folder");

            started.Clear();
            string gone = Path.Combine(sandbox, "reveal-gone");
            run.False(ShellLauncher.TryReveal(Path.Combine(gone, "x.log"), gone, started.Add, out string error), "neither there: refused");
            run.Equal(error, Path.Combine(gone, "x.log"), "naming the log");
            run.Equal(started.Count, 0, "and nothing is started");

            run.False(
                ShellLauncher.TryReveal(log, folder, _ => throw new System.ComponentModel.Win32Exception(), out string refused),
                "a shell that refuses is a refusal, not a throw");
            run.Equal(refused, folder, "naming what could not be opened");
        });

        run.Case("a search over an existing but logless root opens it", () =>
        {
            string root = Path.Combine(sandbox, "logless");
            Directory.CreateDirectory(root);

            OpenLogsTarget target = BundleLogSearch.ResolveTarget(root);

            run.Equal(target.FolderPath, root, "the root opens");
            run.True(target.RevealFilePath is null, "with nothing selected");
        });
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
