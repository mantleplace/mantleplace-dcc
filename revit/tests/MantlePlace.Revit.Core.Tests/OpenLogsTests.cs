using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Logs button — which log it selects, and what it says when there is not one.
/// </summary>
/// <remarks>
/// The interesting case is the empty one. An empty bundle cache is not an error and must not read
/// as one, but it is also not self-explanatory: the run the curator is looking for may have logged
/// beside a zip on their desktop, and an empty Explorer window says nothing about that.
/// </remarks>
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

            // The cache root holds the bundle, its .part and its sidecar beside the logs. Revealing a
            // zip instead of a log would put the curator one folder from the answer and looking at
            // the wrong file.
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

            // The sentence has to carry the other half, because the folder cannot: a curator whose
            // run logged beside a zip on their desktop is looking at a window that is empty and
            // correct at the same time.
            run.True(
                target.Notice!.Contains("beside that zip", StringComparison.Ordinal),
                "naming where a hand-picked zip logs instead");
        });

        run.Case("the index finds the newest log anywhere under the cache root", () =>
        {
            string root = Path.Combine(sandbox, "index");
            string older = Path.Combine(root, "order-a", "bundle.zip" + LocalBundleSource.ImportLogSuffix);
            string newer = Path.Combine(root, "order-b", "bundle.zip" + LocalBundleSource.ProbeLogSuffix);

            Write(older, "an earlier run");
            Write(newer, "a later run");
            Write(Path.Combine(root, "order-b", "bundle.zip"), "not a log");

            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            OpenLogsTarget target = BundleLogSearch.ResolveTarget(root);

            run.Equal(target.RevealFilePath, newer, "the probe log written last wins");
            run.Equal(target.FolderPath, Path.Combine(root, "order-b"), "and its folder is what opens");
        });

        run.Case("an index over a root that does not exist explains rather than throws", () =>
        {
            string absent = Path.Combine(sandbox, "absent");

            OpenLogsTarget target = BundleLogSearch.ResolveTarget(absent);

            run.Equal(target.FolderPath, absent, "the folder to create and open");
            run.True(target.RevealFilePath is null, "with nothing to select");
            run.True(target.Notice is not null, "and a sentence saying so");
        });

        // ShellLauncher.TryReveal's "the log is gone, open its folder instead" guard is deliberately
        // NOT covered here. Asserting it means calling it, and calling it starts File Explorer —
        // which a hosted runner should never be asked to do for a passing test. It is a guard around
        // a shell call, and a shell call is the one thing this headless suite cannot stand in for.

        run.Case("an index over an existing but logless root opens it", () =>
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
