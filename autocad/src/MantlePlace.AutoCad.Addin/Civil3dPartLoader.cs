using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.DatabaseServices;

namespace MantlePlace.AutoCad.Addin;

internal static class Civil3dPartLoader
{
    private const string PartFileName = "MantlePlace.AutoCad.Civil3d.dll";
    private const string PartTypeName = "MantlePlace.AutoCad.Civil3d.Civil3dPart";

    private static Type? loaded;

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
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "The Civil 3D part is off: it threw " + ex.GetType().Name + ": " + ex.Message;
        }
    }

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

    internal static string ReadZoneCode(Type part, Database database) =>
        Convert.ToString(Call(part, "ReadZoneCode", database), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    internal static string ReadDrawingUnits(Type part, Database database) =>
        Convert.ToString(Call(part, "ReadDrawingUnits", database), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    internal static (string Name, int Points) BuildSurfaceFromLandXml(Type part, Database database, string stem, string landXmlPath, string landXmlSurface, string layer) =>
        ((string, int))Call(part, "BuildSurfaceFromLandXml", database, stem, landXmlPath, landXmlSurface, layer)!;

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
