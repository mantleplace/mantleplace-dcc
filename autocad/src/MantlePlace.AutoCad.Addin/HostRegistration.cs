using System.Reflection;
using System.Runtime.CompilerServices;
using MantlePlace.AutoCad;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Tells the shared code which plugin loaded it: <c>autocad</c>, at this assembly's version, importing
/// up to the manifest major <see cref="AutoCadImportPlan.NewestImportedMajor"/>.
/// </summary>
/// <remarks>
/// The shared Core and Client are one assembly for every .NET host (shared/), so the host id the
/// sign-in, the client header and the connected-host presence carry is set here rather than compiled
/// in. A module initializer runs before any type in this assembly is touched, so no static field of
/// the add-in can reach the platform first. The version is this assembly's informational version, the
/// one autocad/Directory.Build.props stamps, raw: the client header drops the build metadata itself.
/// </remarks>
internal static class HostRegistration
{
    /// <summary>The host id the platform contracts name for this plugin.</summary>
    internal const string HostId = AutoCadBlockReader.HostKey;

#pragma warning disable CA2255 // The shell is an application entry assembly to AutoCAD, not a library.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => HostIdentity.Set(
        HostId,
        typeof(HostRegistration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        // 3.x is imported from its catalog and frames, so a 3.0 send is taken and the web is told so.
        AutoCadImportPlan.NewestImportedMajor);
}
