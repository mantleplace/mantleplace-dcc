using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Whether the bundle an export names is ready to import, read from the order row alone (the
/// platform export contract, section 3).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Ready is a fact about the order row, and nothing else counts.</b> Not a download route's
/// answer: a host view of a bundle still being built comes back <c>200</c> as a partial, which is no
/// error and not importable. A plugin that took the route's word would import half a bundle and
/// report <c>imported</c>.
/// </para>
/// <para>
/// Ready is <c>status = "fulfilled"</c>, or <c>status = "paid"</c> with
/// <c>processing_phase = "ready"</c>. Every other row, whatever its status, is not yet: the export
/// stays at <c>waiting_for_bundle</c> until a row change shows it.
/// </para>
/// </remarks>
public static class BundleReadiness
{
    public const string FulfilledStatus = "fulfilled";

    public const string PaidStatus = "paid";

    public const string ReadyPhase = "ready";

    /// <summary>The rule over the two columns.</summary>
    public static bool IsReady(string? status, string? processingPhase)
        => string.Equals(status, FulfilledStatus, StringComparison.Ordinal)
           || (string.Equals(status, PaidStatus, StringComparison.Ordinal)
               && string.Equals(processingPhase, ReadyPhase, StringComparison.Ordinal));

    /// <summary>
    /// The rule over an order row as a row change carries it (snake_case columns). A body that is
    /// not an object, or states neither column, is not ready.
    /// </summary>
    public static bool IsReadyRow(string rowJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rowJson ?? string.Empty);
            JsonElement row = document.RootElement;
            return row.ValueKind == JsonValueKind.Object
                   && IsReady(row.Str("status"), row.Str("processing_phase"));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Whether this plugin's reader takes a bundle, asked before the export moves on.</summary>
/// <remarks>
/// <para>
/// The vault listing carries each bundle's manifest version (<c>null</c> when unknown), so the plugin
/// can say a bundle is unreadable before it opens the import dialog. A version it cannot read fails
/// the export as <c>unsupported_version</c>. An unknown version is allowed through, as the web allows
/// it: the reader decides when the bundle is open.
/// </para>
/// <para>
/// "Cannot read" is the reader's own refusal, from the same two constants: below the floor, a major
/// above the newest it takes, or not a <c>MAJOR.MINOR.PATCH</c> version at all (an integer from the
/// pre-history, a pre-release tag).
/// </para>
/// </remarks>
public static class ExportBundles
{
    /// <summary>Whether the reader takes a bundle of this manifest version.</summary>
    public static bool CanRead(string? manifestVersion)
    {
        if (string.IsNullOrWhiteSpace(manifestVersion))
        {
            return true;
        }

        ManifestVersion parsed = ManifestVersion.Parse(manifestVersion);
        return parsed.IsValid
               && !ManifestVersion.IsBelowFloor(manifestVersion, ManifestVersions.MinSupportedManifestVersion)
               && parsed.Major <= ManifestVersions.NewestSupportedMajor;
    }

    /// <summary>
    /// The transition for a bundle of this manifest version: <c>null</c> when the reader takes it,
    /// otherwise the failure as <c>unsupported_version</c> with words the curator can act on.
    /// </summary>
    public static ExportTransition? RefuseUnreadable(string? manifestVersion)
        => CanRead(manifestVersion)
            ? null
            : ExportTransitions.UnsupportedVersion(
                $"This bundle's manifest version ({ManifestVersion.Describe(manifestVersion)}) is outside what this plugin reads "
                + $"({ManifestVersions.MinSupportedManifestVersion} to {ManifestVersions.NewestSupportedMajor}.x). "
                + "Procure the bundle again, or update the Mantle Place plugin.");
}
