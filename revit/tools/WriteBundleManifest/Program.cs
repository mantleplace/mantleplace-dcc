using System.Reflection;
using System.Text;
using MantlePlace.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: WriteBundleManifest <output path>");
    return 2;
}

string? informational = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
if (string.IsNullOrWhiteSpace(informational))
{
    Console.Error.WriteLine("This tool's assembly carries no version.");
    return 1;
}

string version = BuildIdentity.Parse(informational).Version;
string manifest = AutodeskBundleManifest.Build(version, AutodeskBundleManifest.SupportedRevitYears, BundleHost.Revit);

File.WriteAllText(args[0], manifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(version);
return 0;
