using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// What the About dialog says about which build is loaded and where it came from.
/// </summary>
/// <remarks>
/// The dialog itself cannot be tested without Revit. Its sentences can, and they are the half that
/// matters: the reason this exists is a machine running a build a day older than the source tree,
/// with nothing on screen saying so. Every line here is one that, if it read wrong, would send a
/// maintainer chasing a bug that is already fixed.
/// </remarks>
internal static class InstalledBuildTests
{
    private const string Sha = "c0e8438ca9b56278590780959cf4e5cbaf394675";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the informational version splits into version, sha and dirty", () =>
        {
            BuildIdentity clean = BuildIdentity.Parse($"0.1.0+{Sha}");
            run.Equal(clean.Version, "0.1.0", "the release-shaped half");
            run.Equal(clean.Sha, Sha, "the commit the SDK appended");
            run.Equal(clean.ShortSha, "c0e8438", "seven characters, what a human reads out");
            run.False(clean.Dirty, "no marker means a clean tree");

            BuildIdentity dirty = BuildIdentity.Parse($"0.1.0+{Sha}-dirty");
            run.True(dirty.Dirty, "the marker the build appends when revit/ had uncommitted changes");
            run.Equal(dirty.Sha, Sha, "and the sha is still the sha, without the marker");
        });

        run.Case("a version with no metadata is a version and nothing else", () =>
        {
            BuildIdentity bare = BuildIdentity.Parse("0.1.0");
            run.Equal(bare.Version, "0.1.0", "the whole string");
            run.True(bare.Sha is null, "no commit is known, and none is invented");
            run.Equal(bare.ShortSha, "unknown", "so the short form says so rather than crashing on Substring");
            run.False(bare.Dirty, "unknown is not dirty");
        });

        run.Case("the install stamp parses what the deploy script writes", () =>
        {
            InstallStamp? stamp = InstallStamp.Parse(
                "{\"schema\":1,\"source\":\"source-tree\",\"version\":\"0.1.0+" + Sha + "\","
                + "\"sha\":\"" + Sha + "\",\"dirty\":false,\"branch\":\"feat/ribbon\","
                + "\"sourceTree\":\"D:\\\\GHW\\\\mantleplace-dcc\\\\feat-ribbon\","
                + "\"configuration\":\"Debug\",\"installedAt\":\"2026-09-17T10:12:00-05:00\"}");
            run.True(stamp is not null, "a well-formed stamp reads");
            run.Equal(stamp!.Branch, "feat/ribbon", "the branch the install came from");
            run.Equal(stamp.Sha, Sha, "the commit");
            run.False(stamp.Dirty, "the tree state at deploy time");
            run.Equal(stamp.SourceTree, "D:\\GHW\\mantleplace-dcc\\feat-ribbon", "the worktree");
            run.Equal(stamp.InstalledAt, "2026-09-17T10:12:00-05:00", "when, as written");
            run.True(stamp.FromSourceTree, "and it was a maintainer deploy, not a release zip");
        });

        run.Case("a release stamp has no branch and says so", () =>
        {
            InstallStamp? stamp = InstallStamp.Parse(
                "{\"schema\":1,\"source\":\"release\",\"version\":\"0.1.0+" + Sha + "\","
                + "\"installedAt\":\"2026-09-17T10:12:00-05:00\"}");
            run.True(stamp is not null, "a release stamp reads");
            run.False(stamp!.FromSourceTree, "a release install");
            run.True(stamp.Branch is null, "no branch: a release has no tree to drift from");
            run.True(stamp.Sha is null, "and the sha lives in the version, not repeated here");
        });

        run.Case("a malformed stamp is null, never a throw", () =>
        {
            run.True(InstallStamp.Parse("not json") is null, "garbage");
            run.True(InstallStamp.Parse("[]") is null, "the wrong shape");
            run.True(InstallStamp.Parse("{\"schema\":2}") is null, "a schema this build does not read");
            run.True(InstallStamp.Parse("{\"schema\":1}") is null, "a stamp with no source is not a stamp");
            run.True(InstallStamp.Parse(string.Empty) is null, "an empty file");
        });

        run.Case("About names the commit, and says when the tree was dirty", () =>
        {
            BuildIdentity clean = BuildIdentity.Parse($"0.1.0+{Sha}");
            run.Equal(
                InstalledBuild.Describe(clean, null),
                "Add-in version 0.1.0, commit c0e8438.\n"
                    + "No install stamp beside the add-in: installed by hand, or from a release zip.",
                "no stamp means hand-copied or a release, and both are said");

            BuildIdentity dirty = BuildIdentity.Parse($"0.1.0+{Sha}-dirty");
            run.True(
                InstalledBuild.Describe(dirty, null).StartsWith(
                    "Add-in version 0.1.0, commit c0e8438 with uncommitted changes.",
                    StringComparison.Ordinal),
                "dirty is said in the first sentence, where a screenshot shows it");
        });

        run.Case("About names the branch and tree a source deploy came from", () =>
        {
            BuildIdentity build = BuildIdentity.Parse($"0.1.0+{Sha}");
            InstallStamp stamp = InstallStamp.Parse(
                "{\"schema\":1,\"source\":\"source-tree\",\"version\":\"0.1.0+" + Sha + "\","
                + "\"sha\":\"" + Sha + "\",\"dirty\":false,\"branch\":\"main\","
                + "\"sourceTree\":\"D:\\\\GHW\\\\mantleplace-dcc\\\\main\","
                + "\"configuration\":\"Debug\",\"installedAt\":\"2026-09-17T10:12:00-05:00\"}")!;
            run.Equal(
                InstalledBuild.Describe(build, stamp),
                "Add-in version 0.1.0, commit c0e8438.\n"
                    + "Installed 2026-09-17T10:12:00-05:00 from main (Debug) at D:\\GHW\\mantleplace-dcc\\main.",
                "branch, configuration and tree, so a preview cannot be mistaken for main");
        });

        run.Case("a stamp naming a different commit is called out, not trusted", () =>
        {
            // The files beside the stamp were replaced by hand after the deploy. The stamp is now
            // a lie about the assembly, and the assembly is the thing that is loaded.
            BuildIdentity build = BuildIdentity.Parse($"0.1.0+{Sha}");
            InstallStamp stamp = InstallStamp.Parse(
                "{\"schema\":1,\"source\":\"source-tree\",\"version\":\"0.1.0+abc\","
                + "\"sha\":\"1234567deadbeef\",\"dirty\":false,\"branch\":\"main\","
                + "\"sourceTree\":\"D:\\\\x\",\"configuration\":\"Debug\","
                + "\"installedAt\":\"2026-09-17T10:12:00-05:00\"}")!;
            run.Equal(
                InstalledBuild.Describe(build, stamp),
                "Add-in version 0.1.0, commit c0e8438.\n"
                    + "The install stamp names commit 1234567, not this one: the files beside it were "
                    + "replaced by hand after that deploy.",
                "the assembly wins, and the stamp's claim is reported as the discrepancy it is");
        });

        run.Case("a release stamp says release, and when", () =>
        {
            BuildIdentity build = BuildIdentity.Parse($"0.1.0+{Sha}");
            InstallStamp stamp = InstallStamp.Parse(
                "{\"schema\":1,\"source\":\"release\",\"version\":\"0.1.0+" + Sha + "\","
                + "\"installedAt\":\"2026-09-17T10:12:00-05:00\"}")!;
            run.Equal(
                InstalledBuild.Describe(build, stamp),
                "Add-in version 0.1.0, commit c0e8438.\n"
                    + "Installed 2026-09-17T10:12:00-05:00 from a release zip.",
                "a curator's install has no branch to name");
        });

        run.Case("the Client reads the stamp beside the add-in, and reads nothing as null", () =>
        {
            // The one file read behind About, kept in Client so CI builds it (revit/CLAUDE.md).
            string folder = Path.Combine(Path.GetTempPath(), "mantleplace-stamp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                run.True(
                    MantlePlace.Revit.Client.InstallStampFile.Read(folder) is null,
                    "an add-ins folder with no stamp: hand-copied, or a release");
                run.True(
                    MantlePlace.Revit.Client.InstallStampFile.Read(null) is null,
                    "and no folder at all, which is what an in-memory assembly reports");

                File.WriteAllText(
                    Path.Combine(folder, MantlePlace.Revit.Client.InstallStampFile.FileName),
                    "{\"schema\":1,\"source\":\"source-tree\",\"sha\":\"" + Sha + "\",\"branch\":\"main\","
                        + "\"installedAt\":\"2026-09-17T10:12:00-05:00\"}");
                InstallStamp? stamp = MantlePlace.Revit.Client.InstallStampFile.Read(folder);
                run.True(stamp is not null, "the stamp the deploy script wrote reads back");
                run.Equal(stamp!.Branch, "main", "with its fields");

                File.WriteAllText(Path.Combine(folder, MantlePlace.Revit.Client.InstallStampFile.FileName), "{");
                run.True(
                    MantlePlace.Revit.Client.InstallStampFile.Read(folder) is null,
                    "a truncated stamp is null, never a throw inside a Revit dialog");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });

        return run.Report("installed build");
    }
}
