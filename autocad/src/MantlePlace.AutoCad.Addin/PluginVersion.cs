using System.Reflection;

namespace MantlePlace.AutoCad.Addin;

internal static class PluginVersion
{
    internal static string Current { get; } = Read();

    private static string Read()
    {
        Assembly assembly = typeof(PluginVersion).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
