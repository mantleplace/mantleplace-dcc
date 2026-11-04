// The spike's AutoCAD-only half: the MANTLEPLACEC3DPROBE command. It writes result.md into the run
// folder Invoke-Civil3dProbe.ps1 made, one flushed line at a time so a host that dies mid-run still
// leaves what it reached, and done.txt when it ends. It is inert unless that driver set the variables.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(MantlePlace.AutoCad.Civil3dSpike.Civil3dProbe))]

namespace MantlePlace.AutoCad.Civil3dSpike;

public sealed class Civil3dProbe
{
    private const string DirVariable = "MANTLEPLACE_C3D_PROBE_DIR";
    private static bool ran;

    [CommandMethod("MANTLEPLACEC3DPROBE")]
    public void Run()
    {
        string? dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrEmpty(dir) || ran)
        {
            return;
        }

        ran = true;
        string result = Path.Combine(dir, "result.md");
        void Line(string text) => File.AppendAllText(result, text + Environment.NewLine);

        try
        {
            Line($"# Civil 3D probe, {Environment.GetEnvironmentVariable("MANTLEPLACE_C3D_PROBE_LABEL")}");
            Line("");
            Line($"- Runtime: {RuntimeInformation.FrameworkDescription}");
            Line($"- Process: {Environment.ProcessPath}");
            Line($"- Command line: `{Environment.CommandLine}`");
            Line($"- ACADVER: {Application.GetSystemVariable("ACADVER")}; PRODUCT: {Application.GetSystemVariable("PRODUCT")}");
            Line("");
            Line("## Presence");
            Line("");

            PresenceObservations seen = Observe(Line);
            PresenceVerdict verdict = Presence.Decide(seen);
            Line($"- Verdict: **{verdict}**");
            Line("");

            if (verdict == PresenceVerdict.Civil3dLoaded)
            {
                RunCivilPart(dir, Line);
            }
            else if (Environment.GetEnvironmentVariable("MANTLEPLACE_C3D_PROBE_FORCE") == "1")
            {
                Line("## The Civil 3D part, loaded against the verdict (what a wrong presence test does)");
                Line("");
                RunCivilPart(dir, Line);
            }
            else
            {
                Line("The Civil 3D part was not loaded: the presence test says Civil 3D is not in this process.");
            }
        }
        catch (System.Exception ex)
        {
            Line($"- The probe threw: `{ex.GetType().Name}: {ex.Message}`");
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "done.txt"), "done");
        }
    }

    private static PresenceObservations Observe(Action<string> line)
    {
        string[] managed = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .Select(assembly => assembly.GetName().Name ?? "")
            .Where(name => name.StartsWith("Aecc", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        bool managedLoaded = managed.Contains("AeccDbMgd");
        bool nativeLoaded = GetModuleHandle("AeccLand.dbx") != IntPtr.Zero;
        bool aecBaseLoaded = GetModuleHandle("AecBase.dbx") != IntPtr.Zero;
        string product = $"{Application.GetSystemVariable("PRODUCT")}";
        string hostDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        bool onDisk = File.Exists(Path.Combine(hostDir, "C3D", "AeccDbMgd.dll"));

        string[] linked = [];
        try
        {
            linked = SystemObjects.DynamicLinker.GetLoadedModules()
                .Cast<string>()
                .Select(Path.GetFileName)
                .Where(name => name != null && name.StartsWith("Aec", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .ToArray();
        }
        catch (System.Exception ex)
        {
            line($"- `DynamicLinker.GetLoadedModules` threw: `{ex.GetType().Name}: {ex.Message}`");
        }

        line($"- `AeccDbMgd` loaded as a managed assembly: {managedLoaded} (Aecc assemblies loaded: {managed.Length}{(managed.Length > 0 ? ", e.g. " + string.Join(", ", managed.Take(6)) : "")})");
        line($"- `AeccLand.dbx` a loaded module of the process: {nativeLoaded}");
        line($"- `AecBase.dbx` a loaded module of the process: {aecBaseLoaded}");
        line($"- `DynamicLinker.GetLoadedModules`, Aec modules: {linked.Length}{(linked.Length > 0 ? " (" + string.Join(", ", linked.Take(8)) + ")" : "")}");
        line($"- `PRODUCT`: {product}");
        line($"- `C3D\\AeccDbMgd.dll` on disk beside the host: {onDisk}");
        return new PresenceObservations(managedLoaded, nativeLoaded, aecBaseLoaded, product, onDisk);
    }

    private static void RunCivilPart(string dir, Action<string> line)
    {
        string part = Path.Combine(Path.GetDirectoryName(typeof(Civil3dProbe).Assembly.Location)!, "MantlePlace.Scratch.Civil3dPart.dll");
        try
        {
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(Civil3dProbe).Assembly) ?? AssemblyLoadContext.Default;
            Assembly assembly = context.LoadFromAssemblyPath(part);
            MethodInfo run = assembly.GetType("MantlePlace.AutoCad.Civil3dSpike.Civil3dPart", throwOnError: true)!.GetMethod("Run")!;
            run.Invoke(null, [line]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            line($"- The Civil 3D part threw: `{ex.InnerException.GetType().Name}: {ex.InnerException.Message}`");
        }
        catch (System.Exception ex)
        {
            line($"- Loading the Civil 3D part threw: `{ex.GetType().Name}: {ex.Message}`");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
