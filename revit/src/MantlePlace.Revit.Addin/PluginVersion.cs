using System.Reflection;

namespace MantlePlace.Revit.Addin;

/// <summary>The build that is actually loaded, as a string fit for the head of a diagnostic file.</summary>
/// <remarks>
/// <para>
/// ⛔ Until this existed the assemblies carried no <c>&lt;Version&gt;</c> at all, so every build ever
/// deployed reported MSBuild's default <c>1.0.0.0</c> — not "unknown", but a confident claim to be a
/// 1.0. The only honest way to tell two deployed builds apart was the file timestamp, which is why
/// <c>revit/README.md</c> has to tell you to go and look at one.
/// </para>
/// <para>
/// It matters more here than in a host that can be rebuilt from CI. A pile of Revit API calls in this
/// tree compile but are proven only by a real import (<c>revit/CLAUDE.md</c> lists them), so the first
/// question about any of them throwing is "which build?" — and the artifact that reaches us is a log
/// file, not a machine we can inspect.
/// </para>
/// <para>
/// <b>The commit sha is kept, not trimmed.</b> The SDK appends it to
/// <see cref="AssemblyInformationalVersionAttribute"/> as semver build metadata, so this reads
/// <c>0.1.0+a36fcd8…</c>. A bare version fails in exactly the case that matters most: every dev build
/// between two releases says <c>0.1.0</c> and they are not the same code. The sha names the source
/// commit, which is what the timestamp was standing in for and could never actually say. The release
/// tag and asset name use the version alone — <c>Package-MantlePlaceRevit.ps1</c> trims the metadata
/// there, where it genuinely is not part of the version.
/// </para>
/// </remarks>
internal static class PluginVersion
{
    /// <summary>Read once: the loaded assembly's version cannot change under a running Revit.</summary>
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

        // Reached only if informational-version generation is ever switched off. Falling back to the
        // zero-padded AssemblyVersion beats printing nothing; saying "unknown" beats inventing one.
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
