using System.Text.Json;

namespace MantlePlace.Core;

public static class BundleReadiness
{
    public const string FulfilledStatus = "fulfilled";

    public const string PaidStatus = "paid";

    public const string ReadyPhase = "ready";

    public static bool IsReady(string? status, string? processingPhase)
        => string.Equals(status, FulfilledStatus, StringComparison.Ordinal)
           || (string.Equals(status, PaidStatus, StringComparison.Ordinal)
               && string.Equals(processingPhase, ReadyPhase, StringComparison.Ordinal));

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

public static class ExportBundles
{
    public static bool CanRead(string? manifestVersion)
    {
        if (string.IsNullOrWhiteSpace(manifestVersion))
        {
            return true;
        }

        ManifestVersion parsed = ManifestVersion.Parse(manifestVersion);
        return parsed.IsValid
               && !ManifestVersion.IsBelowFloor(manifestVersion, ManifestVersions.MinSupportedManifestVersion)
               && parsed.Major <= HostIdentity.NewestImportedMajor;
    }

    public static ExportTransition? RefuseUnreadable(string? manifestVersion)
        => CanRead(manifestVersion)
            ? null
            : ExportTransitions.UnsupportedVersion(
                $"This bundle's manifest version ({ManifestVersion.Describe(manifestVersion)}) is outside what this plugin reads "
                + $"({ManifestVersions.MinSupportedManifestVersion} to {HostIdentity.NewestImportedMajor}.x). "
                + "Procure the bundle again, or update the Mantle Place plugin.");
}
