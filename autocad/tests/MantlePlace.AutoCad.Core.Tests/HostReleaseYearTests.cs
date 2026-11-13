using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>The release year the plugin reports to the platform and the feedback form, from AutoCAD's version.</summary>
internal static class HostReleaseYearTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("each supported release maps to its own year, not 2000 plus the major version", () =>
        {
            run.Equal(HostReleaseYear.From(25, 0), "2025", "R25.0");
            run.Equal(HostReleaseYear.From(25, 1), "2026", "R25.1 is 2026, not 2025");
            run.Equal(HostReleaseYear.From(26, 0), "2027", "R26.0 is 2027, not 2026");
        });

        run.Case("a release the plugin does not support gives no year rather than a guess", () =>
        {
            run.Equal(HostReleaseYear.From(24, 3), string.Empty, "R24.3 (2024)");
            run.Equal(HostReleaseYear.From(26, 1), string.Empty, "R26.1, not yet released");
        });

        return run.Report("HostReleaseYear");
    }
}
