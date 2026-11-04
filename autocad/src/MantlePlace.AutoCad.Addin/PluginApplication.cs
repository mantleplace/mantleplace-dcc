using Autodesk.AutoCAD.Runtime;
using MantlePlace.AutoCad.Addin;

[assembly: ExtensionApplication(typeof(PluginApplication))]
[assembly: CommandClass(typeof(PluginCommands))]

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The plugin's entry point, run once when AutoCAD loads the bundle at startup.
/// </summary>
/// <remarks>
/// <para>
/// Starts the session, the vault client and the connected host (<see cref="MantlePlaceApplication"/>),
/// and stops them again. Nothing here waits on the network: the stored session is restored on the
/// pool, and the connected host joins when a sign-in lands.
/// </para>
/// <para>
/// Whether Civil 3D is in the process is still decided when a command first needs it, not here,
/// because it was not tested whether <c>AeccLand.dbx</c> is already loaded when a bundle's assembly
/// initialises at startup (<c>autocad/docs/api-record.md</c>).
/// </para>
/// </remarks>
public sealed class PluginApplication : IExtensionApplication
{
    /// <inheritdoc />
    public void Initialize() => MantlePlaceApplication.Start();

    /// <inheritdoc />
    public void Terminate() => MantlePlaceApplication.Stop();
}
