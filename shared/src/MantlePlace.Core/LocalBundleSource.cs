namespace MantlePlace.Core;

public static class LocalBundleSource
{
    public const string PathVariable = "MANTLEPLACE_BUNDLE_ZIP";

    public static string? Unattended(string? variableValue)
        => string.IsNullOrWhiteSpace(variableValue) ? null : variableValue.Trim();

    public static string LogPathFor(string zipPath) => zipPath + ImportLogSuffix;

    public static string ProbeLogPathFor(string zipPath) => zipPath + ProbeLogSuffix;

    public const string ImportLogSuffix = ".mantleplace-import.log";

    public const string ProbeLogSuffix = ".mantleplace-terrain-probe.log";

    public static bool IsLogFileName(string fileName)
        => fileName.EndsWith(ImportLogSuffix, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(ProbeLogSuffix, StringComparison.OrdinalIgnoreCase);
}
