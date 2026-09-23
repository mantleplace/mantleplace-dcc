using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the vault window says about each bundle, which rows its search keeps, and how many its
/// status line says are shown.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the shim because CI never builds the shim (<c>HPS-02</c>), and two of these
/// rules are the kind that fail by looking fine: a null size written as <c>0 B</c>, and a search that
/// quietly matched the order id as well as the label.
/// </para>
/// <para>
/// The cache state is taken as text, already said: deciding it reads the disk, which is
/// <c>MantlePlace.Revit.Client</c>'s, and this assembly does no I/O.
/// </para>
/// </remarks>
public static class VaultRows
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Whether a bundle's row stays in the list under this query.
    /// </summary>
    /// <remarks>
    /// The area label only, ignoring case, after trimming the query — a trailing space typed mid-word
    /// should not empty the list. Not the order id, even on a row shown under its id: an id is not
    /// what a curator names an area by, and the tooltip says the label is what is searched. An empty
    /// or whitespace query keeps every row.
    /// </remarks>
    public static bool Matches(string? query, VaultBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return bundle.AoiLabel.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rows the list shows under this query, and which of them is selected.
    /// </summary>
    /// <typeparam name="TRow">The window's own row type; this only needs its bundle.</typeparam>
    /// <param name="rows">A row for every bundle the listing gave, in listing order.</param>
    /// <param name="bundleOf">The bundle a row describes.</param>
    /// <param name="query">The search field's text.</param>
    /// <param name="selectedOrderId">The order selected before the list changed, if any.</param>
    /// <remarks>
    /// ⛔ The selection is carried by order id and returned as an index into <em>the shown rows</em>,
    /// never the listing: under a filter the two disagree, and an index read against the wrong one is
    /// Prepare, Import or Remove landing on a bundle the curator did not pick. A selected bundle the
    /// filter hides comes back unselected (<c>-1</c>) rather than selected out of sight, and a
    /// refresh, which hands over new rows, finds the same order wherever it now sits.
    /// </remarks>
    public static VaultView<TRow> Show<TRow>(
        IReadOnlyList<TRow> rows,
        Func<TRow, VaultBundle> bundleOf,
        string? query,
        string? selectedOrderId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(bundleOf);

        List<TRow> shown = [.. rows.Where(row => Matches(query, bundleOf(row)))];
        int selected = selectedOrderId is null
            ? -1
            : shown.FindIndex(row => string.Equals(bundleOf(row).OrderId, selectedOrderId, StringComparison.Ordinal));

        return new VaultView<TRow>(shown, selected);
    }

    /// <summary>
    /// One vault row: label, area, size, manifest version, status and cache state.
    /// </summary>
    /// <param name="bundle">The listed bundle.</param>
    /// <param name="cacheState">What the cache says about it, as the status line would say it.</param>
    /// <remarks>
    /// A row with no label is shown under its order id, so it is never a blank line.
    /// </remarks>
    public static string Describe(VaultBundle bundle, string cacheState)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        string label = bundle.AoiLabel.Length > 0 ? bundle.AoiLabel : bundle.OrderId;
        string area = bundle.AreaKm2 is { } km2
            ? km2.ToString("0.##", CultureInfo.InvariantCulture) + " km²"
            : WindowLabels.AreaUnknown;

        return $"{label} — {area} — {SizeText(bundle.SizeBytes)} — {VersionText(bundle.ManifestVersion)} — {bundle.Status} — {cacheState}";
    }

    /// <summary>
    /// A download size in decimal units — <c>48.3 MB</c> — or the unknown words.
    /// </summary>
    /// <remarks>
    /// ⛔<c>HPS-20</c>: <c>null</c> is unknown, and says so; it is never <c>0 B</c> and never blank. A
    /// negative size is not a size either, and the listing reader does not bound it, so it is unknown
    /// too. Decimal units, one decimal place, because the row states a size rather than an exact
    /// byte count, and the exact count is what the cache checks against.
    /// </remarks>
    public static string SizeText(long? bytes)
    {
        if (bytes is not { } known || known < 0)
        {
            return WindowLabels.SizeUnknown;
        }

        double value = known;
        int unit = 0;

        // Step up while the value would round to 1000 or more, so 999,960 bytes is "1 MB" rather
        // than "1000 KB".
        while (unit < SizeUnits.Length - 1 && Math.Round(value, 1) >= 1000)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
    }

    /// <summary>
    /// The manifest version exactly as the listing gave it — <c>19</c> and <c>1.2.0</c> alike — or
    /// the unknown words.
    /// </summary>
    /// <remarks>
    /// Shown, never judged: nothing is enabled or disabled by it here. The import gate is the one
    /// place a version decides anything (<see cref="VaultBundle.ManifestVersion"/>).
    /// </remarks>
    public static string VersionText(string? version)
        => string.IsNullOrWhiteSpace(version) ? WindowLabels.ManifestVersionUnknown : $"Manifest {version}";

    /// <summary>
    /// The status line after a listing or a change of filter: <c>3 of 12 bundle(s).</c> while a filter
    /// is active, <c>12 bundle(s).</c> when not.
    /// </summary>
    /// <param name="shown">Rows the filter keeps.</param>
    /// <param name="total">Rows the listing gave.</param>
    /// <param name="skipped">Rows the listing reader skipped as unreadable.</param>
    /// <param name="filtering">Whether the search field holds a query.</param>
    /// <remarks>
    /// ⛔<c>HPS-21</c>: the skipped rows are said whatever the filter shows. A filter narrows what the
    /// curator is looking at; it does not make platform corruption stop being true.
    /// </remarks>
    public static string CountLine(int shown, int total, int skipped, bool filtering)
    {
        string count = filtering ? $"{shown} of {total} bundle(s)." : $"{total} bundle(s).";
        return skipped == 0 ? count : $"{count} {skipped} row(s) were unreadable and skipped.";
    }
}

/// <summary>What the vault list shows: the rows a query keeps, and the index among them that is selected (<c>-1</c> for none).</summary>
public sealed record VaultView<TRow>(IReadOnlyList<TRow> Shown, int SelectedIndex);
