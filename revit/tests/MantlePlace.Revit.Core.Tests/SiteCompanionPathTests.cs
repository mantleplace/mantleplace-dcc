using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Coverage for the one rule that keeps three Revit versions out of each other's IFC companion: the
/// converted <c>.rvt</c> is named for the Revit that produced it.
/// </summary>
/// <remarks>
/// <para>
/// The bug this pins is silent and one-way. Every version used to convert into the same
/// <c>Site.rvt</c> inside the shared per-order cache, so the second Revit to import an order
/// UPGRADED the first one's companion in place — and a <c>.rvt</c> upgrade cannot be undone. The
/// older project's link then failed to reload, in Manage Links rather than at import, and
/// re-importing did not repair it because the file still existed.
/// </para>
/// <para>
/// Every path here is built with <see cref="Path.Combine(string[])"/> rather than written as a
/// <c>C:\…</c> literal. The suite runs on <c>ubuntu-latest</c> as well as on Windows, and a
/// backslash is an ordinary file-name character there — so a literal would make the directory and
/// stem assertions pass vacuously on the very runner that reports the required <c>pure-core</c>
/// check.
/// </para>
/// </remarks>
internal static class SiteCompanionPathTests
{
    /// <summary>The extracted site IFC, at the shape the archive writes.</summary>
    private static readonly string IfcPath = Path.Combine("cache", "order", "extracted", "Site", "Site.ifc");

    /// <summary>Its directory, for the assertions about where a companion lands.</summary>
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

        run.Case("the companion sits beside the IFC, so Remove download still takes it", () =>
        {
            // The sweep deletes the whole per-order root recursively. Keeping the companion inside
            // `extracted/` is what makes that continue to be true without a second place to sweep.
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
            // Autodesk returns "2025". If it ever returns something with a separator in it, the
            // answer is the mapping every other string-to-path in this host uses — neutralised and
            // suffixed so it still cannot collide — not a traversal, and not a throw that would
            // abandon the step and every step after it.
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

        run.Case("a dotted version and the token it could be confused with stay distinct", () =>
        {
            // The dots are mapped BEFORE sanitisation precisely so this holds. Stripping them
            // afterwards would land "2025.1" and "2025-1" on one companion.
            run.True(
                !string.Equals(
                    SiteCompanionPath.ForVersion(IfcPath, "2025.1"),
                    SiteCompanionPath.ForVersion(IfcPath, "2025-1"),
                    StringComparison.OrdinalIgnoreCase),
                "a dotted version is not the dashed one");
        });

        run.Case("every companion this build writes is one this build recognises", () =>
        {
            // The round trip is the property that matters, and it is the one that broke first: a
            // version token with a dot in it is more than one component, so ForVersion wrote a
            // companion IsCompanionOf then refused — and the re-import called CreateFromIFC against
            // an already-linked path, which throws.
            foreach (string version in (string[])["2025", "2026", "2027", "2025.1", "a/b", "..", "1.2.3"])
            {
                run.True(
                    SiteCompanionPath.IsCompanionOf(IfcPath, SiteCompanionPath.ForVersion(IfcPath, version)),
                    $"the companion for version \"{version}\" is recognised");
            }
        });

        run.Case("a companion written by ANOTHER Revit version is still recognised", () =>
        {
            // The case that makes this a shape rule rather than an equality: a project created in
            // 2025 links Site.2025.rvt, and re-importing it under 2027 must still see that link.
            run.True(SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.2025.rvt")), "2025's");
            run.True(SiteCompanionPath.IsCompanionOf(IfcPath, Path.Combine(IfcDirectory, "Site.2027.rvt")), "2027's");
        });

        run.Case("the version-neutral companion an older build wrote is recognised", () =>
        {
            // Nothing writes this any more. Recognising it is what leaves a project imported before
            // per-version companions alone, instead of calling CreateFromIFC against its live link.
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
            // Site.v2.final.2026.rvt belongs to Site.v2.final.ifc, not to Site.v2.ifc. Without the
            // no-dot rule the shorter stem would claim it and a re-import would decline to build a
            // link the project does not have.
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
