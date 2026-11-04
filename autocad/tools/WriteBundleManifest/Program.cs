using System.Reflection;
using System.Text;
using MantlePlace.AutoCad;
using MantlePlace.Revit.Core;

// Usage: WriteBundleManifest <output PackageContents.xml path> <output MantlePlace.cuix path> <monogram folder>
//
// Writes the partial CUIX file the manifest names (the Plug-Ins tab panel; the monogram renders it
// shows are read from the folder, which is the Revit plugin's Resources, so no image is stored twice),
// then the Autodesk bundle manifest for the version the Core assembly was BUILT with, across the
// supported AutoCAD years, and prints that version on one line. The package script compares it with
// the version Directory.Build.props declares and stops when they differ: a stale build, not a
// stale manifest, is the failure this exists to catch.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: WriteBundleManifest <PackageContents.xml path> <MantlePlace.cuix path> <monogram folder>");
    return 2;
}

string? informational = typeof(AutodeskBundleManifest).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
if (string.IsNullOrWhiteSpace(informational))
{
    Console.Error.WriteLine("The core assembly carries no version.");
    return 1;
}

string version = BuildIdentity.Parse(informational).Version;

Dictionary<string, byte[]> images = [];
foreach (string name in PlugInsPanel.ImageSources)
{
    string path = Path.Combine(args[2], name);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"The panel shows {name} and it is not in {args[2]}.");
        return 1;
    }

    images[name] = File.ReadAllBytes(path);
}

File.WriteAllBytes(args[1], CuixPackage.Build(images));

string manifest = AutodeskBundleManifest.Build(
    version,
    AutodeskBundleManifest.SupportedYearsFor(BundleHost.AutoCad),
    BundleHost.AutoCad);

// No BOM, LF endings as built: Autodesk's loader reads it as plain UTF-8.
File.WriteAllText(args[0], manifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(version);
return 0;
