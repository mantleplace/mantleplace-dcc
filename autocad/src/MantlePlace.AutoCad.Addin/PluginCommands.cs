using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The shell's commands. Only one so far: it proves the bundle loaded, which build loaded, and
/// whether the Civil 3D part is on.
/// </summary>
public sealed class PluginCommands
{
    /// <summary>Says which Mantle Place build is loaded and whether the Civil 3D part is on.</summary>
    [CommandMethod("MANTLEPLACEABOUT")]
    public void About()
    {
        string informational = typeof(PluginCommands).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        BuildIdentity build = BuildIdentity.Parse(informational);
        string dirty = build.Dirty ? ", with uncommitted changes" : string.Empty;
        string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        // Decided here, at first use, not when the bundle loads: see PluginApplication.
        PresenceVerdict verdict = Presence.Decide(HostObservations.Observe());

        Say($"Mantle Place for AutoCAD {build.Version} ({build.ShortSha}{dirty}), on {runtime}.");
        Say(Civil3dPartLoader.Describe(verdict));
    }

    private static void Say(string text)
        => Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + text + "\n");
}
