using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Loads the Civil 3D part, only when the presence test says Civil 3D is in the process.
/// </summary>
/// <remarks>
/// The part is never referenced by the shell, so AutoCAD never resolves a Civil 3D assembly while
/// loading it. A wrong verdict degrades rather than crashes: loading the part under Civil 3D's
/// "as AutoCAD" launcher threw <see cref="FileNotFoundException"/> for <c>AeccDbMgd</c> in the spike
/// and the host carried on, so every failure here is reported as text and the shell stays up.
/// </remarks>
internal static class Civil3dPartLoader
{
    private const string PartFileName = "MantlePlace.AutoCad.Civil3d.dll";
    private const string PartTypeName = "MantlePlace.AutoCad.Civil3d.Civil3dPart";
    private const string DescribeMethod = "Describe";

    /// <summary>
    /// What to say about Civil 3D for <paramref name="verdict"/>: the part's own description when it
    /// may load and does, otherwise why it is not in use.
    /// </summary>
    internal static string Describe(PresenceVerdict verdict)
    {
        if (!Presence.MayLoadCivil3dPart(verdict))
        {
            return verdict == PresenceVerdict.InstalledNotLoaded
                ? "Civil 3D is installed but not running in this session, so its part is off."
                : "Civil 3D is not present, so its part is off.";
        }

        try
        {
            string part = Path.Combine(Path.GetDirectoryName(typeof(Civil3dPartLoader).Assembly.Location) ?? string.Empty, PartFileName);
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(Civil3dPartLoader).Assembly) ?? AssemblyLoadContext.Default;
            Assembly assembly = context.LoadFromAssemblyPath(part);
            MethodInfo describe = assembly.GetType(PartTypeName, throwOnError: true)!.GetMethod(DescribeMethod)!;
            return "The Civil 3D part is on: " + Convert.ToString(describe.Invoke(null, null), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return "The Civil 3D part is off: it threw " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or TypeLoadException or InvalidOperationException or NullReferenceException)
        {
            return "The Civil 3D part is off: loading it threw " + ex.GetType().Name + ": " + ex.Message;
        }
    }
}
