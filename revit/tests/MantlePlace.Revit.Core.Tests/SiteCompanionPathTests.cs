using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Coverage for the one rule that keeps three Revit versions out of each other's IFC companion: the
/// converted <c>.rvt</c> is named for the Revit that produced it.
/// </summary>
/// <remarks>
/// The bug this pins is silent and one-way. Every version used to convert into the same
/// <c>Site.rvt</c> inside the shared per-order cache, so the second Revit to import an order
/// UPGRADED the first one's companion in place — and a <c>.rvt</c> upgrade cannot be undone. The
/// older project's link then failed to reload, in Manage Links rather than at import, and
/// re-importing did not repair it because the file still existed.
/// </remarks>
internal static class SiteCompanionPathTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the companion carries the Revit version that produced it", () =>
        {
            run.Equal(
                SiteCompanionPath.ForVersion(@"C:\cache\order\extracted\Site\Site.ifc", "2025"),
                @"C:\cache\order\extracted\Site\Site.2025.rvt",
                "2025's companion");
        });

        run.Case("two Revit versions never name the same file", () =>
        {
            const string ifcPath = @"C:\cache\order\extracted\Site\Site.ifc";

            string[] companions =
            [
                SiteCompanionPath.ForVersion(ifcPath, "2025"),
                SiteCompanionPath.ForVersion(ifcPath, "2026"),
                SiteCompanionPath.ForVersion(ifcPath, "2027"),
            ];

            run.Equal(
                companions.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                3,
                "2025, 2026 and 2027 each get their own companion");
        });

        run.Case("the companion sits beside the IFC, so Remove download still takes it", () =>
        {
            // The sweep deletes the whole per-order root recursively. Keeping the companion inside
            // `extracted/` is what makes that continue to be true without a second place to sweep.
            const string ifcPath = @"C:\cache\order\extracted\Site\Site.ifc";
            string companion = SiteCompanionPath.ForVersion(ifcPath, "2027");

            run.Equal(
                Path.GetDirectoryName(companion),
                Path.GetDirectoryName(ifcPath),
                "companion directory");
        });

        run.Case("the version-neutral path is still derivable, because older projects link to it", () =>
        {
            // Nothing writes this any more. It is computed so the shim can recognise a link that a
            // build before this fix created and leave it alone, rather than trying to create a
            // second link at the same IFC and taking the rest of the import down with it.
            run.Equal(
                SiteCompanionPath.VersionNeutral(@"C:\cache\order\extracted\Site\Site.ifc"),
                @"C:\cache\order\extracted\Site\Site.rvt",
                "the path builds before this fix wrote");
        });

        run.Case("a version-qualified companion is never mistaken for the version-neutral one", () =>
        {
            const string ifcPath = @"C:\cache\order\extracted\Site\Site.ifc";

            run.True(
                !string.Equals(
                    SiteCompanionPath.ForVersion(ifcPath, "2025"),
                    SiteCompanionPath.VersionNeutral(ifcPath),
                    StringComparison.OrdinalIgnoreCase),
                "qualified and neutral are different files");
        });

        run.Case("an entry name with dots keeps only its own extension replaced", () =>
        {
            run.Equal(
                SiteCompanionPath.ForVersion(@"C:\cache\extracted\Site.v2.final.ifc", "2026"),
                @"C:\cache\extracted\Site.v2.final.2026.rvt",
                "a dotted stem survives");
        });

        run.Case("a bare file name yields a bare companion", () =>
        {
            run.Equal(SiteCompanionPath.ForVersion("Site.ifc", "2025"), "Site.2025.rvt", "no directory");
        });

        run.Case("a version string that is not a safe path token goes through HPS-30", () =>
        {
            // Autodesk returns "2025". If it ever returns something with a separator in it, the
            // answer is the same mapping every other string-to-path in this host uses — neutralised
            // and suffixed so it still cannot collide — not a traversal and not a throw that would
            // abandon the step.
            string companion = SiteCompanionPath.ForVersion(@"C:\cache\extracted\Site.ifc", "../2025");
            string fileName = Path.GetFileName(companion);

            run.Equal(Path.GetDirectoryName(companion), @"C:\cache\extracted", "stays in its directory");
            run.True(
                fileName.IndexOfAny([.. Path.GetInvalidFileNameChars()]) < 0,
                "the separators are gone, so the result is one file name and not a traversal");
        });

        run.Case("two unsafe version strings that map alike still get different companions", () =>
        {
            const string ifcPath = @"C:\cache\extracted\Site.ifc";

            run.True(
                !string.Equals(
                    SiteCompanionPath.ForVersion(ifcPath, "a/b"),
                    SiteCompanionPath.ForVersion(ifcPath, "a:b"),
                    StringComparison.OrdinalIgnoreCase),
                "the collision suffix carries the difference the mapping loses");
        });

        run.Case("a companion written by ANOTHER Revit version is still recognised", () =>
        {
            // The case that makes this a shape rule rather than an equality: a project created in
            // 2025 links Site.2025.rvt, and re-importing it under 2027 must still see that link.
            const string ifcPath = @"C:\cache\extracted\Site\Site.ifc";

            run.True(SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\cache\extracted\Site\Site.2025.rvt"), "2025's");
            run.True(SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\cache\extracted\Site\Site.2027.rvt"), "2027's");
            run.True(SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\cache\extracted\Site\Site.rvt"), "the neutral one");
            run.True(
                SiteCompanionPath.IsCompanionOf(ifcPath, SiteCompanionPath.ForVersion(ifcPath, "a/b")),
                "a sanitised version token");
        });

        run.Case("something that is not this IFC's companion is not claimed", () =>
        {
            const string ifcPath = @"C:\cache\extracted\Site\Site.ifc";

            run.False(SiteCompanionPath.IsCompanionOf(ifcPath, ifcPath), "the IFC is not its own companion");
            run.False(
                SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\cache\extracted\Site\Other.2025.rvt"),
                "another model's companion");
            run.False(
                SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\elsewhere\Site.2025.rvt"),
                "the right name in the wrong directory");
            run.False(
                SiteCompanionPath.IsCompanionOf(ifcPath, @"C:\cache\extracted\Site\Site.2025.ifc"),
                "the right name with the wrong extension");
            run.False(
                SiteCompanionPath.IsCompanionOf(ifcPath, ""),
                "a link whose path did not resolve");
        });

        run.Case("the extra component may not itself be dotted, so a longer stem does not match", () =>
        {
            // Site.v2.final.2026.rvt belongs to Site.v2.final.ifc, not to Site.v2.ifc. Without the
            // no-dot rule the shorter stem would claim it and a re-import would decline to build a
            // link the project does not have.
            run.False(
                SiteCompanionPath.IsCompanionOf(@"C:\cache\extracted\Site.v2.ifc", @"C:\cache\extracted\Site.v2.final.2026.rvt"),
                "a dotted tail is a different model");
            run.True(
                SiteCompanionPath.IsCompanionOf(@"C:\cache\extracted\Site.v2.final.ifc", @"C:\cache\extracted\Site.v2.final.2026.rvt"),
                "and the model it really belongs to still matches");
        });

        run.Case("an empty version or an empty IFC path is a caller bug, not a path", () =>
        {
            run.True(Refuses(() => SiteCompanionPath.ForVersion("Site.ifc", "")), "an empty version");
            run.True(Refuses(() => SiteCompanionPath.ForVersion("", "2025")), "an empty IFC path");
            run.True(Refuses(() => SiteCompanionPath.VersionNeutral("")), "an empty IFC path, neutral");
        });

        return run.Report("IFC site companion paths");
    }

    /// <summary>True when <paramref name="body"/> refuses its arguments rather than returning a path.</summary>
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
