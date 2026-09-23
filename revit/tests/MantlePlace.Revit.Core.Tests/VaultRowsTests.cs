using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The vault window's rows, its search and its status line: what a row says about a bundle, which
/// rows a query keeps, and how many the line says are shown.
/// </summary>
/// <remarks>
/// ⛔<c>HPS-20</c> is the reason this is not left to the shim: a null size is unknown, not zero, and
/// a row that read <c>0 B</c> for a bundle the listing gave no size for would be the plugin appearing
/// to work. Only a test can hold that, and CI never builds the window.
/// </remarks>
internal static class VaultRowsTests
{
    private const string CacheText = "Not downloaded.";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("search matches the area label, ignoring case", () =>
        {
            VaultBundle bundle = Bundle("order-1", "Harbour Front");

            run.True(VaultRows.Matches("harbour", bundle), "lower case finds a capitalised label");
            run.True(VaultRows.Matches("HARBOUR FRONT", bundle), "upper case finds it too");
            run.True(VaultRows.Matches("bour fr", bundle), "a substring across the space matches");
            run.False(VaultRows.Matches("harbor", bundle), "a different spelling does not");
        });

        run.Case("an empty or whitespace query keeps every row", () =>
        {
            VaultBundle labelled = Bundle("order-1", "Harbour Front");
            VaultBundle unlabelled = Bundle("order-2", string.Empty);

            foreach (string? query in new[] { null, string.Empty, " ", "\t  " })
            {
                run.True(VaultRows.Matches(query, labelled), $"\"{query}\" keeps a labelled row");
                run.True(VaultRows.Matches(query, unlabelled), $"\"{query}\" keeps an unlabelled row");
            }
        });

        run.Case("search does not match the order id, even on a row shown under it", () =>
        {
            // An unlabelled row is shown under its order id, and the curator may well type what they
            // see. The brief is the label only: an id is not what a curator names an area by.
            VaultBundle unlabelled = Bundle("ord_7f3a", string.Empty);
            VaultBundle labelled = Bundle("ord_7f3a", "Harbour Front");

            run.False(VaultRows.Matches("ord_7f3a", unlabelled), "the id alone does not match an unlabelled row");
            run.False(VaultRows.Matches("7f3a", labelled), "nor a labelled one");
            run.True(VaultRows.Describe(unlabelled, CacheText).StartsWith("ord_7f3a", StringComparison.Ordinal), "the unlabelled row is still shown under its id");
        });

        run.Case("the typed text is trimmed before it is matched", () =>
        {
            // A trailing space from typing "Harbour " and pausing should not empty the list.
            VaultBundle bundle = Bundle("order-1", "Harbour Front");
            run.True(VaultRows.Matches("  front ", bundle), "surrounding whitespace is ignored");
        });

        run.Case("under a filter, the selected index names the selected bundle, not the listing's row at that index", () =>
        {
            // ⛔ The failure this guards: the window used to read the selection as an index into the
            // whole listing. With "front" typed, the one shown row is index 0, and the listing's row
            // 0 is a different bundle — Prepare would have built the wrong order.
            VaultBundle[] listing = [Bundle("a", "Airport"), Bundle("b", "Bridge"), Bundle("c", "Harbour Front")];

            VaultView<VaultBundle> view = VaultRows.Show(listing, row => row, "front", "c");
            run.Equal(view.Shown.Count, 1, "one row matches");
            run.Equal(view.SelectedIndex, 0, "it is selected, at its shown position");
            run.Equal(view.Shown[view.SelectedIndex].OrderId, "c", "and that position is the selected order");
            run.False(string.Equals(listing[view.SelectedIndex].OrderId, "c", StringComparison.Ordinal), "the listing's row at the same index is someone else");
        });

        run.Case("a selected bundle the filter hides is deselected, not selected out of sight", () =>
        {
            VaultBundle[] listing = [Bundle("a", "Airport"), Bundle("c", "Harbour Front")];

            VaultView<VaultBundle> view = VaultRows.Show(listing, row => row, "air", "c");
            run.Equal(view.SelectedIndex, -1, "an action now says pick a bundle first rather than acting on a hidden one");
        });

        run.Case("after a refresh the filter still holds, and the selection finds its order wherever it now sits", () =>
        {
            // A refresh hands over new row objects, and the platform may have added orders above the
            // selected one. Selection is by order id, so it follows the order, not the position.
            VaultBundle[] before = [Bundle("a", "Harbour North"), Bundle("c", "Harbour Front")];
            VaultView<VaultBundle> first = VaultRows.Show(before, row => row, "harbour", "c");
            run.Equal(first.SelectedIndex, 1, "selected before the refresh");

            VaultBundle[] after = [Bundle("n", "Harbour East"), Bundle("x", "Bridge"), Bundle("a", "Harbour North"), Bundle("c", "Harbour Front")];
            VaultView<VaultBundle> second = VaultRows.Show(after, row => row, "harbour", first.Shown[first.SelectedIndex].OrderId);
            run.Equal(second.Shown.Count, 3, "the filter is applied to the new listing");
            run.Equal(second.Shown[second.SelectedIndex].OrderId, "c", "the same order is selected");
            run.Equal(second.SelectedIndex, 2, "at its new shown position");
        });

        run.Case("with no filter and no selection, every row is shown and none selected", () =>
        {
            VaultBundle[] listing = [Bundle("a", "Airport"), Bundle("b", string.Empty)];
            VaultView<VaultBundle> view = VaultRows.Show(listing, row => row, string.Empty, null);
            run.Equal(view.Shown.Count, 2, "every row, the unlabelled one included");
            run.Equal(view.SelectedIndex, -1, "nothing selected");
        });

        run.Case("a known size reads in a unit a curator reads", () =>
        {
            run.Equal(VaultRows.SizeText(0), "0 B", "a real zero is known, and says so");
            run.Equal(VaultRows.SizeText(512), "512 B", "under a kilobyte stays in bytes");
            run.Equal(VaultRows.SizeText(1_500), "1.5 KB", "kilobytes");
            run.Equal(VaultRows.SizeText(134_217_728), "134.2 MB", "the listing's own 134 MB example");
            run.Equal(VaultRows.SizeText(2_000_000_000), "2 GB", "gigabytes, with no trailing .0");
            run.Equal(VaultRows.SizeText(999_960), "1 MB", "a value that rounds up to the next unit says the next unit");
        });

        run.Case("⛔HPS-20: a null size reads as unknown, never as zero or blank", () =>
        {
            string text = VaultRows.SizeText(null);
            run.Equal(text, WindowLabels.SizeUnknown, "the unknown words");
            run.False(text.Contains('0', StringComparison.Ordinal), "no zero anywhere in it");

            string row = VaultRows.Describe(Bundle("order-1", "Harbour Front", size: null), CacheText);
            run.Contains(row, WindowLabels.SizeUnknown, "the row says the size is unknown");
            run.False(row.Contains(" 0 B", StringComparison.Ordinal), "and never 0 B");
        });

        run.Case("a negative size is not a size, and reads as unknown", () =>
        {
            run.Equal(VaultRows.SizeText(-1), WindowLabels.SizeUnknown, "the listing does not bound it, so the row must");
        });

        run.Case("the version is shown as the listing gave it, in both families", () =>
        {
            run.Equal(VaultRows.VersionText("19"), "Manifest 19", "a legacy integer, as given");
            run.Equal(VaultRows.VersionText("1.2.0"), "Manifest 1.2.0", "a semantic version, as given");
            run.Equal(VaultRows.VersionText(null), WindowLabels.ManifestVersionUnknown, "null is unknown");
            run.Equal(VaultRows.VersionText("  "), WindowLabels.ManifestVersionUnknown, "blank is unknown");
        });

        run.Case("a row names the label, area, size, version, status and cache state, in that order", () =>
        {
            VaultBundle bundle = Bundle("order-1", "Harbour Front", size: 48_300_000, version: "1.2.0", areaKm2: 1.25);

            run.Equal(
                VaultRows.Describe(bundle, CacheText),
                "Harbour Front — 1.25 km² — 48.3 MB — Manifest 1.2.0 — Available — Not downloaded.",
                "the whole row");
        });

        run.Case("a row with nothing known says so field by field", () =>
        {
            VaultBundle bundle = Bundle("order-1", string.Empty, size: null, version: null, areaKm2: null);

            run.Equal(
                VaultRows.Describe(bundle, CacheText),
                $"order-1 — {WindowLabels.AreaUnknown} — {WindowLabels.SizeUnknown} — {WindowLabels.ManifestVersionUnknown} — Available — Not downloaded.",
                "each unknown in its own words");
        });

        run.Case("the status line counts every bundle when no filter is active", () =>
        {
            run.Equal(VaultRows.CountLine(12, 12, 0, filtering: false), "12 bundle(s).", "no filter, nothing skipped");
            run.Equal(
                VaultRows.CountLine(12, 12, 2, filtering: false),
                "12 bundle(s). 2 row(s) were unreadable and skipped.",
                "⛔HPS-21: the skipped rows are said");
        });

        run.Case("the status line says N of M while a filter is active, and keeps the skipped rows", () =>
        {
            run.Equal(VaultRows.CountLine(3, 12, 0, filtering: true), "3 of 12 bundle(s).", "a narrowing filter");
            run.Equal(VaultRows.CountLine(12, 12, 0, filtering: true), "12 of 12 bundle(s).", "a filter that keeps all is still a filter");
            run.Equal(
                VaultRows.CountLine(0, 12, 1, filtering: true),
                "0 of 12 bundle(s). 1 row(s) were unreadable and skipped.",
                "⛔HPS-21 survives the filter");
        });

        return run.Report("vault rows");
    }

    private static VaultBundle Bundle(
        string orderId,
        string label,
        long? size = 1_000,
        string? version = "1.0.0",
        double? areaKm2 = 1)
        => new()
        {
            OrderId = orderId,
            AoiLabel = label,
            AreaKm2 = areaKm2,
            Status = BundleStatus.Available,
            SizeBytes = size,
            ManifestVersion = version,
        };
}
