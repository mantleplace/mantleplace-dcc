using System.Reflection;
using System.Runtime.CompilerServices;
using MantlePlace.AutoCad;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

internal static class HostRegistration
{
    internal const string HostId = AutoCadBlockReader.HostKey;

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => HostIdentity.Set(
        HostId,
        typeof(HostRegistration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        ManifestVersions.AutoCadNewestImportedMajor);
}
