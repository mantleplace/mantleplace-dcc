using System.Globalization;

namespace MantlePlace.Core;

public static class VaultRows
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    public static bool Matches(string? query, VaultBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return bundle.AoiLabel.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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

    public static string Describe(VaultBundle bundle, string cacheState)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        string label = bundle.AoiLabel.Length > 0 ? bundle.AoiLabel : bundle.OrderId;
        string area = bundle.AreaKm2 is { } km2
            ? km2.ToString("0.##", CultureInfo.InvariantCulture) + " km²"
            : WindowLabels.AreaUnknown;

        return $"{label} — {area} — {SizeText(bundle.SizeBytes)} — {VersionText(bundle.ManifestVersion)} — {bundle.Status} — {cacheState}";
    }

    public static string SizeText(long? bytes)
    {
        if (bytes is not { } known || known < 0)
        {
            return WindowLabels.SizeUnknown;
        }

        double value = known;
        int unit = 0;

        while (unit < SizeUnits.Length - 1 && Math.Round(value, 1) >= 1000)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
    }

    public static string VersionText(string? version)
        => string.IsNullOrWhiteSpace(version) ? WindowLabels.ManifestVersionUnknown : $"Manifest {version}";

    public static string CountLine(int shown, int total, int skipped, string? query)
    {
        string count = string.IsNullOrWhiteSpace(query) ? $"{total} bundle(s)." : $"{shown} of {total} bundle(s).";
        return skipped == 0 ? count : $"{count} {skipped} row(s) were unreadable and skipped.";
    }
}

public sealed record VaultView<TRow>(IReadOnlyList<TRow> Shown, int SelectedIndex);
