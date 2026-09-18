using System.Text;
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The file the tree step hands to <c>LoadFamily</c>: written by rename, verified before it is
/// trusted, one per build, and the older builds' copies removed.
/// </summary>
internal static class FamilyFileStoreTests
{
    private const string FileName = "Mantle Place Tree.rfa";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-family-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
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

        return run.Report("family file store");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        byte[] family = Encoding.UTF8.GetBytes("pretend this is a family");

        run.Case("the family is written under its own name, in a folder for its build, with no .part left", () =>
        {
            string root = Path.Combine(sandbox, "fresh");
            string path = FamilyFileStore.Materialise(root, "build-a", FileName, family);

            run.Equal(path, Path.Combine(root, "build-a", FileName), "the name Revit will give the family");
            run.True(File.ReadAllBytes(path).SequenceEqual(family), "the bytes it was given");
            run.False(File.Exists(path + ".part"), "promoted by rename");
        });

        run.Case("an identical file already there is reused, not rewritten", () =>
        {
            string root = Path.Combine(sandbox, "reuse");
            string path = FamilyFileStore.Materialise(root, "build-a", FileName, family);
            DateTime written = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, written);

            FamilyFileStore.Materialise(root, "build-a", FileName, family);
            run.True(File.GetLastWriteTimeUtc(path) == written, "left alone");
        });

        run.Case("a damaged file is replaced rather than trusted", () =>
        {
            string root = Path.Combine(sandbox, "damaged");
            string path = FamilyFileStore.Materialise(root, "build-a", FileName, family);
            File.WriteAllText(path, "truncated");

            FamilyFileStore.Materialise(root, "build-a", FileName, family);
            run.True(File.ReadAllBytes(path).SequenceEqual(family), "rewritten from the bytes it was given");
        });

        run.Case("an older build's copy is removed, and nothing else in the root is", () =>
        {
            string root = Path.Combine(sandbox, "builds");
            FamilyFileStore.Materialise(root, "build-old", FileName, family);
            File.WriteAllText(Path.Combine(root, "not-a-build.txt"), "someone else's");

            FamilyFileStore.Materialise(root, "build-new", FileName, family);
            run.False(Directory.Exists(Path.Combine(root, "build-old")), "the older build is gone");
            run.True(File.Exists(Path.Combine(root, "not-a-build.txt")), "a loose file is not a build");
            run.True(File.Exists(Path.Combine(root, "build-new", FileName)), "the new one stands");
        });
    }
}
