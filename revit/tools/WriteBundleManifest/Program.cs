using System.Reflection;
using System.Text;
using MantlePlace.Core;

// Usage: WriteBundleManifest <output PackageContents.xml path>
//
// Writes the Autodesk bundle manifest for the version this tool was BUILT with (the host tree's Directory.Build.props, the plugin's own), across the
// supported Revit years, and prints that version on one line. The package script compares it with
// the version Directory.Build.props declares and stops when they differ: a stale build, not a
// stale manifest, is the failure this exists to catch.
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

// No BOM, LF endings as built: Autodesk's loader reads it as plain UTF-8.
File.WriteAllText(args[0], manifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(version);
return 0;
