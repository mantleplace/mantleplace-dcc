using System.Reflection;
using System.Runtime.CompilerServices;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class HostRegistration
{
    internal const string HostId = "revit";

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => HostIdentity.Set(
        HostId,
        typeof(HostRegistration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        ManifestVersions.RevitNewestImportedMajor);
}
