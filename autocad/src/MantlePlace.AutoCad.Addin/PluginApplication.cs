using Autodesk.AutoCAD.Runtime;
using MantlePlace.AutoCad.Addin;

[assembly: ExtensionApplication(typeof(PluginApplication))]
[assembly: CommandClass(typeof(PluginCommands))]

namespace MantlePlace.AutoCad.Addin;

public sealed class PluginApplication : IExtensionApplication
{
    public void Initialize() => MantlePlaceApplication.Start();

    public void Terminate() => MantlePlaceApplication.Stop();
}
