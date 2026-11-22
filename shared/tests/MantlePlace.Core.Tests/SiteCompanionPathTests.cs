using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SiteCompanionPathTests
{
    private static readonly string IfcPath = Path.Combine("cache", "order", "extracted", "Site", "Site.ifc");

    private static readonly string IfcDirectory = Path.GetDirectoryName(IfcPath)!;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the companion carries the Revit version that produced it", () =>
        {
            run.Equal(
                SiteCompanionPath.ForVersion(IfcPath, "2025"),
                Path.Combine(IfcDirectory, "Site.2025.rvt"),
                "2025's companion");
        });

        run.Case("two Revit versions never name the same file", () =>
        {
            string[] companions =
            [
                SiteCompanionPath.ForVersion(IfcPath, "2025"),
                SiteCompanionPath.ForVersion(IfcPath, "2026"),
                SiteCompanionPath.ForVersion(IfcPath, "2027"),
            ];

            run.Equal(
                companions.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                3,
                "2025, 2026 and 2027 each get their own companion");
        });

        run.Case("the companion sits beside the IFC, so Remove Download still takes it", () =>
        {
            run.Equal(
                Path.GetDirectoryName(SiteCompanionPath.ForVersion(IfcPath, "2027")),
                IfcDirectory,
                "companion directory");
        });

        run.Case("an entry name with dots keeps only its own extension replaced", () =>
        {
            run.Equal(
                SiteCompanionPath.ForVersion(Path.Combine("cache", "Site.v2.final.ifc"), "2026"),
                Path.Combine("cache", "Site.v2.final.2026.rvt"),
                "a dotted stem survives");
        });

        run.Case("a bare file name yields a bare companion", () =>
        {
            run.Equal(SiteCompanionPath.ForVersion("Site.ifc", "2025"), "Site.2025.rvt", "no directory");
        });

        run.Case("a version string that is not a safe path token is neutralised, not refused", () =>
        {
            string companion = SiteCompanionPath.ForVersion(IfcPath, "../2025");

            run.Equal(Path.GetDirectoryName(companion), IfcDirectory, "stays in its directory");
            run.True(
                Path.GetFileName(companion).IndexOfAny([.. Path.GetInvalidFileNameChars()]) < 0,
                "the separators are gone, so the result is one file name and not a traversal");
        });

        run.Case("two unsafe version strings that map alike still get different companions", () =>
        {
            run.True(
                !string.Equals(
                    SiteCompanionPath.ForVersion(IfcPath, "a/b"),
                    SiteCompanionPath.ForVersion(IfcPath, "a:b"),
                    StringComparison.OrdinalIgnoreCase),
                "the collision suffix carries the difference the mapping loses");
        });

        run.Case("no two version strings share a companion", () =>
        {
            string[] versions = ["2025", "2025.1", "2025-1", "2025_1", "2025/1", "a.b", "a/b", "a:b", "..", "1.2.3"];

            run.Equal(
                versions.Select(version => SiteCompanionPath.ForVersion(IfcPath, version))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                versions.Length,
                "every version string gets its own companion");
        });

        run.Case("every companion this build writes is one this build recognises", () =>
        {
            foreach (string version in (string[])["2025", "2026", "2027", "2025.1", "a/b", "..", "1.2.3"])
            {
                run.True(
                    SiteCompanionPath.IsCompanionOf(IfcPath, SiteCompanionPath.ForVersion(IfcPath, version)),
                    $"the companion for version \"{version}\" is recognised");
            }
        });

        run.Case("a companion written by ANOTHER Revit version is still recognised", () =>
        {
            run.True(SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.2025.rvt")), "2025's");
            run.True(SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.2027.rvt")), "2027's");
        });

        run.Case("the version-neutral companion an older build wrote is recognised", () =>
        {
            run.True(SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.rvt")), "Site.rvt");
        });

        run.Case("something that is not this IFC's companion is not claimed", () =>
        {
            run.False(SiteCompanionPath.IsCompanionOf(IfcPath, IfcPath), "the IFC is not its own companion");
            run.False(
                SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Other.2025.rvt")),
                "another model's companion");
            run.False(
                SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine("elsewhere", "Site.2025.rvt")),
                "the right name in the wrong directory");
            run.False(
                SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.2025.ifc")),
                "the right name with the wrong extension");
            run.False(
                SiteCompanionPath.IsCompanionOf(IfcPath, ""),
                "a link whose path did not resolve");
        });

        run.Case("the extra component may not itself be dotted, so a longer stem does not match", () =>
        {
            run.False(
                SiteCompanionPath.IsCompanionOf(
                    Path.Combine("cache", "Site.v2.ifc"),
                    Path.Combine("cache", "Site.v2.final.2026.rvt")),
                "a dotted tail is a different model");
            run.True(
                SiteCompanionPath.IsCompanionOf(
                    Path.Combine("cache", "Site.v2.final.ifc"),
                    Path.Combine("cache", "Site.v2.final.2026.rvt")),
                "and the model it really belongs to still matches");
        });

        run.Case("an empty version or an empty IFC path is a caller bug, not a path", () =>
        {
            run.True(Refuses(() => SiteCompanionPath.ForVersion("Site.ifc", "")), "an empty version");
            run.True(Refuses(() => SiteCompanionPath.ForVersion("", "2025")), "an empty IFC path");
            run.True(Refuses(() => SiteCompanionPath.IsCompanionOf("", "Site.rvt")), "an empty IFC path, matching");
        });

        return run.Report("IFC site companion paths");
    }

    private static bool Refuses(Action body)
    {
        try
        {
            body();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
