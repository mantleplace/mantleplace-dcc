using Autodesk.AutoCAD.Runtime;
using MantlePlace.AutoCad.Addin;

[assembly: ExtensionApplication(typeof(PluginApplication))]
[assembly: CommandClass(typeof(PluginCommands))]

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The plugin's entry point, run once when AutoCAD loads the bundle at startup.
/// </summary>
/// <remarks>
/// Deliberately empty. The shell is the base every later feature lands on, and it does nothing at
/// load: whether Civil 3D is in the process is decided when a command first needs it, not here,
/// because it was not tested whether <c>AeccLand.dbx</c> is already loaded when a bundle's assembly
/// initialises at startup (<c>autocad/docs/api-record.md</c>).
/// </remarks>
public sealed class PluginApplication : IExtensionApplication
{
    /// <inheritdoc />
    public void Initialize()
    {
    }

    /// <inheritdoc />
    public void Terminate()
    {
    }
}
