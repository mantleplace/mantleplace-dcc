using Autodesk.Civil.ApplicationServices;

namespace MantlePlace.AutoCad.Civil3d;

/// <summary>
/// The Civil 3D half of the plugin: everything that needs a Civil 3D assembly lives in this assembly
/// and nowhere else. Nothing yet but its shell, so the shell's load path is proven end to end.
/// </summary>
public static class Civil3dPart
{
    /// <summary>
    /// What the part can say about the Civil 3D it loaded into, which resolving a Civil 3D type proves:
    /// the version of the Civil 3D API assembly it is running against.
    /// </summary>
    public static string Describe()
        => "Civil 3D API " + typeof(CivilApplication).Assembly.GetName().Version;
}
