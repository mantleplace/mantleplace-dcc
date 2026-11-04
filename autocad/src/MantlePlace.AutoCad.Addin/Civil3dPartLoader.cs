using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.DatabaseServices;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Loads the Civil 3D part, only when the presence test says Civil 3D is in the process, and calls it.
/// </summary>
/// <remarks>
/// The part is never referenced by the shell, so AutoCAD never resolves a Civil 3D assembly while
/// loading it. A wrong verdict degrades rather than crashes: loading the part under Civil 3D's
/// "as AutoCAD" launcher threw <see cref="FileNotFoundException"/> for <c>AeccDbMgd</c> in the spike
/// and the host carried on, so every failure to load is reported as text and the shell stays up.
/// </remarks>
internal static class Civil3dPartLoader
{
    private const string PartFileName = "MantlePlace.AutoCad.Civil3d.dll";
    private const string PartTypeName = "MantlePlace.AutoCad.Civil3d.Civil3dPart";

    private static Type? loaded;

    /// <summary>
    /// What to say about Civil 3D for <paramref name="verdict"/>: the part's own description when it
    /// may load and does, otherwise why it is not in use.
    /// </summary>
    internal static string Describe(PresenceVerdict verdict)
    {
        if (Load(verdict, out string? problem) is not { } part)
        {
            return problem;
        }

        try
        {
            return "The Civil 3D part is on: " + Convert.ToString(Call(part, "Describe"), System.Globalization.CultureInfo.InvariantCulture);
        }
#pragma warning disable CA1031 // Whatever the part throws is said, never left to take AutoCAD down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "The Civil 3D part is off: it threw " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// The part, when <paramref name="verdict"/> lets it load and it does; otherwise <c>null</c>, and
    /// <paramref name="problem"/> says why it is off.
    /// </summary>
    internal static Type? Load(PresenceVerdict verdict, out string problem)
    {
        problem = string.Empty;
        if (!Presence.MayLoadCivil3dPart(verdict))
        {
            problem = verdict == PresenceVerdict.InstalledNotLoaded
                ? "Civil 3D is installed but not running in this session, so its part is off."
                : "Civil 3D is not present, so its part is off.";
            return null;
        }

        if (loaded is not null)
        {
            return loaded;
        }

        try
        {
            string part = Path.Combine(Path.GetDirectoryName(typeof(Civil3dPartLoader).Assembly.Location) ?? string.Empty, PartFileName);
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(Civil3dPartLoader).Assembly) ?? AssemblyLoadContext.Default;
            loaded = context.LoadFromAssemblyPath(part).GetType(PartTypeName, throwOnError: true)!;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or TypeLoadException or InvalidOperationException or NullReferenceException)
        {
            problem = "The Civil 3D part is off: loading it threw " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>The drawing's zone code, in Civil 3D's spelling. Read only: the part never sets it.</summary>
    internal static string ReadZoneCode(Type part, Database database) =>
        Convert.ToString(Call(part, "ReadZoneCode", database), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A TIN surface built from a LandXML file: the name it was given and the points Civil 3D read.</summary>
    internal static (string Name, int Points) BuildSurfaceFromLandXml(Type part, Database database, string stem, string landXmlPath, string landXmlSurface, string layer) =>
        ((string, int))Call(part, "BuildSurfaceFromLandXml", database, stem, landXmlPath, landXmlSurface, layer)!;

    /// <summary>Calls the part, and rethrows what the part threw rather than the reflection wrapper.</summary>
    private static object? Call(Type part, string method, params object[] arguments)
    {
        MethodInfo target = part.GetMethod(method) ?? throw new MissingMethodException(PartTypeName, method);
        try
        {
            return target.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
