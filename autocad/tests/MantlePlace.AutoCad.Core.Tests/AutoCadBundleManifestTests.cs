using System.Reflection;
using System.Xml.Linq;
using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The <c>PackageContents.xml</c> the AutoCAD release zip carries, from the same function Revit's
/// does given the other <see cref="BundleHost"/>. Packaging and the storefront cannot run in CI, so
/// what can be pinned is the manifest itself: the host parameter changes what it should and nothing
/// else, every supported year is declared with its series bounds and the build it loads, and the
/// version is the tree's one.
/// </summary>
internal static class AutoCadBundleManifestTests
{
    private static readonly int[] Years = [2025, 2026, 2027];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the AutoCAD manifest is rooted at ApplicationPackage, for AutoCAD, named for AutoCAD", () =>
        {
            XElement root = Parse("1.0.0", Years).Root!;

            run.Equal(root.Name.LocalName, "ApplicationPackage", "the root element is Autodesk's");
            run.Equal(Attr(root, "AutodeskProduct"), "AutoCAD", "the bundle is for AutoCAD");
            run.Equal(Attr(root, "Name"), AutodeskBundleManifest.AutoCadAppName, "the app is named for the AutoCAD plugin");
            run.True(
                Attr(root, "Name") != AutodeskBundleManifest.AppName,
                "not Revit's name: Revit's deploy script refuses on that name, so sharing it would refuse a Revit deploy over an AutoCAD bundle");
        });

        run.Case("the host parameter is the only thing that separates the two plugins' manifests", () =>
        {
            string revit = AutodeskBundleManifest.Build("1.0.0", Years, BundleHost.Revit);
            string autoCad = AutodeskBundleManifest.Build("1.0.0", Years, BundleHost.AutoCad);

            run.True(revit != autoCad, "the same version and years give a different manifest per host");

            XElement revitRoot = XDocument.Parse(revit).Root!;
            XElement autoCadRoot = XDocument.Parse(autoCad).Root!;
            run.Equal(Attr(revitRoot, "AutodeskProduct"), "Revit", "Revit's stays Revit's");
            run.True(
                Attr(revitRoot, "ProductCode") != Attr(autoCadRoot, "ProductCode"),
                "each host is its own product to the storefront");
            run.True(
                Attr(revitRoot, "UpgradeCode") != Attr(autoCadRoot, "UpgradeCode"),
                "and has its own upgrade code");
            run.Equal(
                AutodeskBundleManifest.BundleFolderNameFor(BundleHost.Revit),
                AutodeskBundleManifest.BundleFolderName,
                "the Revit bundle folder is the one it always was");
            run.Equal(
                AutodeskBundleManifest.BundleFolderNameFor(BundleHost.AutoCad),
                AutodeskBundleManifest.AutoCadBundleFolderName,
                "the AutoCAD bundle folder is its own, so both bundles can sit in one Autodesk bundle folder");
            run.True(
                AutodeskBundleManifest.BundleFolderNameFor(BundleHost.Revit)
                    != AutodeskBundleManifest.BundleFolderNameFor(BundleHost.AutoCad),
                "two folders");
        });

        run.Case("AutoCAD and Civil 3D 2025 to 2027 are each declared with explicit series bounds", () =>
        {
            XElement[] requirements = [.. Parse("1.0.0", Years).Root!.Element("Components")!.Elements("RuntimeRequirements")];

            run.Equal(requirements.Length, 3, "one runtime requirement per year");

            string[] series = ["R25.0", "R25.1", "R26.0"];
            for (int i = 0; i < Years.Length; i++)
            {
                run.Equal(Attr(requirements[i], "SeriesMin"), series[i], $"{Years[i]} starts its range");
                run.Equal(Attr(requirements[i], "SeriesMax"), series[i], $"{Years[i]} ends it: a one-point range, never open");
                run.Equal(Attr(requirements[i], "Platform"), "AutoCAD|Civil3D", $"{Years[i]} names AutoCAD and Civil 3D");
                run.Equal(Attr(requirements[i], "OS"), "Win64", $"{Years[i]} runs on Windows");
            }
        });

        run.Case("2025 and 2026 load the .NET 8 build and 2027 loads the .NET 10 build", () =>
        {
            XElement components = Parse("1.0.0", Years).Root!.Element("Components")!;
            XElement[] requirements = [.. components.Elements("RuntimeRequirements")];
            XElement[] entries = [.. components.Elements("ComponentEntry").Where(entry => Attr(entry, "ModuleName")!.EndsWith(".dll", StringComparison.Ordinal))];

            run.Equal(entries.Length, 3, "one assembly entry per year");
            string[] folders = ["net8", "net8", "net10"];
            for (int i = 0; i < Years.Length; i++)
            {
                run.Equal(
                    Attr(entries[i], "ModuleName"),
                    $"./Contents/{folders[i]}/{AutodeskBundleManifest.AutoCadModuleFileName}",
                    $"{Years[i]} names the module of the build it loads");
                run.Equal(
                    Attr(entries[i], "ModuleName"),
                    AutodeskBundleManifest.ModulePathFor(BundleHost.AutoCad, Years[i]),
                    $"{Years[i]}: the manifest and the function the package script reads agree");
                run.True(
                    requirements[i].NextNode == entries[i],
                    $"{Years[i]}: the entry follows its own requirement, which is how Autodesk pairs them");
                run.Equal(Attr(entries[i], "AppType"), ".NET", $"{Years[i]} is a .NET assembly");
                run.Equal(Attr(entries[i], "LoadOnAutoCADStartup"), "True", $"{Years[i]} loads at startup");
            }

            run.Equal(AutodeskBundleManifest.AutoCadNet8Folder, "net8", "the .NET 8 build's folder");
            run.Equal(AutodeskBundleManifest.AutoCadNet10Folder, "net10", "the .NET 10 build's folder");
        });

        run.Case("every year also names the partial CUIX file, right after its assembly, so the panel loads with the plugin", () =>
        {
            XElement components = Parse("1.0.0", Years).Root!.Element("Components")!;
            XElement[] cuix = [.. components.Elements("ComponentEntry").Where(entry => Attr(entry, "ModuleName")!.EndsWith(".cuix", StringComparison.Ordinal))];

            run.Equal(cuix.Length, 3, "one CUIX entry per year");
            for (int i = 0; i < cuix.Length; i++)
            {
                run.Equal(Attr(cuix[i], "ModuleName"), AutodeskBundleManifest.AutoCadCuixModulePath, $"{Years[i]} names the one file");
                run.Equal(
                    Attr((XElement)cuix[i].PreviousNode!, "ModuleName"),
                    AutodeskBundleManifest.ModulePathFor(BundleHost.AutoCad, Years[i]),
                    $"{Years[i]}: it follows the assembly, under the same requirement");
                run.Equal(Attr(cuix[i], "LoadOnAutoCADStartup"), "True", $"{Years[i]} loads at startup");
            }

            run.Equal(AutodeskBundleManifest.AutoCadCuixModulePath, "./Contents/" + PlugInsPanel.FileName, "the file the package script writes");
        });

        run.Case("Revit's manifest names no CUIX file", () =>
            run.False(
                AutodeskBundleManifest.Build("1.0.0", [2026], BundleHost.Revit).Contains(".cuix", StringComparison.Ordinal),
                "a CUIX is AutoCAD's"));

        run.Case("the supported years are the three the plugin supports, for both hosts", () =>
        {
            run.Equal(
                string.Join(",", AutodeskBundleManifest.SupportedYearsFor(BundleHost.AutoCad)),
                "2025,2026,2027",
                "AutoCAD and Civil 3D 2025 to 2027");
            run.Equal(
                string.Join(",", AutodeskBundleManifest.SupportedYearsFor(BundleHost.Revit)),
                string.Join(",", AutodeskBundleManifest.SupportedRevitYears),
                "Revit's list is the one it always was");
        });

        run.Case("a year with no declared AutoCAD series is refused rather than guessed", () =>
        {
            run.Throws<ArgumentException>(
                () => AutodeskBundleManifest.Build("1.0.0", [2028], BundleHost.AutoCad),
                "2028 has no series");
            run.Throws<ArgumentException>(
                () => AutodeskBundleManifest.ModulePathFor(BundleHost.AutoCad, 2024),
                "2024 has no module");
            run.Equal(
                AutodeskBundleManifest.SeriesFor(BundleHost.Revit, 2026),
                "R2026",
                "Revit's series stay the year, for any year");
        });

        run.Case("the version it is built from is the version it carries, everywhere it is stated", () =>
        {
            XElement root = Parse("1.4.2", Years).Root!;

            run.Equal(Attr(root, "AppVersion"), "1.4.2", "the package carries the version");
            foreach (XElement entry in root.Descendants("ComponentEntry"))
            {
                run.Equal(Attr(entry, "Version"), "1.4.2", "so does each component entry");
            }
        });

        run.Case("a version is never blank and a range is never empty, for AutoCAD too", () =>
        {
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build(" ", [2025], BundleHost.AutoCad), "blank version");
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build("1.0.0", [], BundleHost.AutoCad), "no years");
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build("1.0.0", [2025, 2025], BundleHost.AutoCad), "a repeated year");
        });

        run.Case("the build is deterministic, and a new version gets a new product code", () =>
        {
            run.Equal(
                AutodeskBundleManifest.Build("1.0.0", Years, BundleHost.AutoCad),
                AutodeskBundleManifest.Build("1.0.0", Years, BundleHost.AutoCad),
                "same input, same bytes");

            string a = Attr(Parse("1.0.0", [2025]).Root!, "ProductCode") ?? string.Empty;
            string b = Attr(Parse("1.0.1", [2025]).Root!, "ProductCode") ?? string.Empty;
            string upgradeA = Attr(Parse("1.0.0", [2025]).Root!, "UpgradeCode") ?? string.Empty;
            string upgradeB = Attr(Parse("1.0.1", [2025]).Root!, "UpgradeCode") ?? string.Empty;

            run.True(a.Length > 0 && a != b, "the storefront upgrades by product code, so each version has its own");
            run.True(upgradeA.Length > 0 && upgradeA == upgradeB, "the upgrade code is the same across versions");
        });

        run.Case("the bundle icon is the monogram, named the same as Revit's", () =>
        {
            string icon = Attr(Parse("1.0.0", [2025]).Root!, "Icon") ?? string.Empty;

            run.Equal(icon, AutodeskBundleManifest.IconBundlePath, "the icon is the one the package script copies in");
            run.True(
                Path.GetFileName(icon).StartsWith("MantlePlaceMark_", StringComparison.Ordinal),
                "it is the Mantle Place mark");
        });

        run.Case("the version this tree declares is a storefront-acceptable one: never 0.x (ADR 0018)", () =>
        {
            string declared = DeclaredVersion();

            run.False(declared.StartsWith("0.", StringComparison.Ordinal), $"declared {declared} must not start with 0.");
            run.True(Version.TryParse(declared, out _), $"declared {declared} is a dotted version");
        });

        return run.Report("AutoCadBundleManifest");
    }

    /// <summary>The version this build declares: the Core assembly's, minus the commit metadata.</summary>
    private static string DeclaredVersion()
        => BuildIdentity.Parse(
            typeof(AutodeskBundleManifest).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion).Version;

    private static XDocument Parse(string version, int[] years)
        => XDocument.Parse(AutodeskBundleManifest.Build(version, years, BundleHost.AutoCad));

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);
}
