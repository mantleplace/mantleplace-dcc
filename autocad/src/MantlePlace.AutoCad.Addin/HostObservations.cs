using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.ApplicationServices.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Reads the host for the presence test (<see cref="Presence"/>): what is loaded in this process and
/// what is installed beside it. Impure and cheap; the decision is the Core's.
/// </summary>
internal static class HostObservations
{
    private const string AeccManagedAssembly = "AeccDbMgd";
    private const string AecBaseModule = "AecBase.dbx";

    /// <summary>Observes the process the shell is loaded in.</summary>
    internal static PresenceObservations Observe()
    {
        bool managedLoaded = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .Any(assembly => string.Equals(assembly.GetName().Name, AeccManagedAssembly, StringComparison.OrdinalIgnoreCase));
        bool nativeLoaded = GetModuleHandle(Presence.DecidingModule) != IntPtr.Zero;
        bool aecBaseLoaded = GetModuleHandle(AecBaseModule) != IntPtr.Zero;
        string product = Convert.ToString(Application.GetSystemVariable("PRODUCT"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        string hostDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
        bool onDisk = File.Exists(Path.Combine(hostDirectory, "C3D", AeccManagedAssembly + ".dll"));

        return new PresenceObservations(managedLoaded, nativeLoaded, aecBaseLoaded, product, onDisk);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
