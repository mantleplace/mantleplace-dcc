using System.Reflection;
using System.Xml.Linq;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The <c>PackageContents.xml</c> that makes the release zip an Autodesk application bundle
/// (ADR 0018: the storefront installs the same zip the curator copies by hand).
/// </summary>
/// <remarks>
/// Packaging and the storefront cannot run in CI, so what can be pinned is the manifest itself:
/// well-formed, carrying the declared version, a module path for every supported year, and an icon
/// that is a file in this tree.
/// </remarks>
internal static class AutodeskBundleManifestTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the manifest is well-formed XML rooted at ApplicationPackage, named Mantle Place", () =>
        {
            XElement root = Parse("1.0.0", [2025, 2026, 2027]).Root!;

            run.Equal(root.Name.LocalName, "ApplicationPackage", "the root element is Autodesk's");
            run.Equal(Attr(root, "Name"), "Mantle Place", "the app is named for the product");
            run.Equal(Attr(root, "AutodeskProduct"), "Revit", "the bundle is for Revit");
            run.Equal(Attr(root, "ProductType"), "Application", "an add-in is an Application");
        });

        run.Case("the version it is built from is the version it carries, everywhere it is stated", () =>
        {
            XElement root = Parse("1.4.2", [2025]).Root!;

            run.Equal(Attr(root, "AppVersion"), "1.4.2", "the package carries the version");
            foreach (XElement entry in root.Descendants("ComponentEntry"))
            {
                run.Equal(Attr(entry, "Version"), "1.4.2", "so does each component entry");
            }
        });

        run.Case("every supported year is declared, each pointing at the module that year loads", () =>
        {
            XElement components = Parse("1.0.0", [2025, 2026, 2027]).Root!.Element("Components")!;
            XElement[] requirements = [.. components.Elements("RuntimeRequirements")];
            XElement[] entries = [.. components.Elements("ComponentEntry")];

            run.Equal(requirements.Length.ToString(), "3", "one runtime requirement per year");
            run.Equal(entries.Length.ToString(), "3", "one component entry per year");

            int[] years = [2025, 2026, 2027];
            for (int i = 0; i < years.Length; i++)
            {
                run.Equal(Attr(requirements[i], "SeriesMin"), $"R{years[i]}", $"{years[i]} starts the range");
                run.Equal(Attr(requirements[i], "SeriesMax"), $"R{years[i]}", $"{years[i]} ends the range");
                run.Equal(Attr(requirements[i], "Platform"), "Revit", $"{years[i]} is a Revit requirement");
                run.Equal(Attr(requirements[i], "OS"), "Win64", $"{years[i]} runs on Windows");
                run.Equal(Attr(entries[i], "ModuleName"), AutodeskBundleManifest.ModulePathFor(years[i]), $"{years[i]} names its module");
                run.True(
                    requirements[i].NextNode == entries[i],
                    $"{years[i]}: the entry follows its own requirement, which is how Autodesk pairs them");
            }
        });

        run.Case("one build serves every year: the module is the add-in manifest inside Contents", () =>
        {
            foreach (int year in AutodeskBundleManifest.SupportedRevitYears)
            {
                run.Equal(
                    AutodeskBundleManifest.ModulePathFor(year),
                    "./Contents/MantlePlace.addin",
                    $"{year} loads the manifest beside the assemblies");
            }
        });

        run.Case("the supported years are the three the plugin supports", () =>
        {
            run.Equal(
                string.Join(",", AutodeskBundleManifest.SupportedRevitYears),
                "2025,2026,2027",
                "the same range as revit/CLAUDE.md");
        });

        run.Case("the bundle icon is the monogram, and that file is in this tree", () =>
        {
            XElement root = Parse("1.0.0", [2025]).Root!;
            string icon = Attr(root, "Icon") ?? string.Empty;

            run.Equal(icon, AutodeskBundleManifest.IconBundlePath, "the icon is the one the package script copies in");
            run.True(
                Path.GetFileName(icon).StartsWith("MantlePlaceMark_", StringComparison.Ordinal),
                "it is the Mantle Place mark, not a ribbon glyph");

            string? resources = RepoTree.Find(
                "src/MantlePlace.Revit.Addin/Resources",
                dir => File.Exists(Path.Combine(dir, AutodeskBundleManifest.IconSourceFileName)));
            run.True(resources is not null, "the monogram render the script copies exists in Resources");
        });

        run.Case("a version is never blank and a range is never empty", () =>
        {
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build(" ", [2025]), "blank version");
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build("1.0.0", []), "no years");
            run.Throws<ArgumentException>(() => AutodeskBundleManifest.Build("1.0.0", [2025, 2025]), "a repeated year");
        });

        run.Case("the build is deterministic, and a new version gets a new product code", () =>
        {
            string first = AutodeskBundleManifest.Build("1.0.0", [2025, 2026]);

            run.Equal(AutodeskBundleManifest.Build("1.0.0", [2025, 2026]), first, "same input, same bytes");

            string a = Attr(Parse("1.0.0", [2025]).Root!, "ProductCode") ?? string.Empty;
            string b = Attr(Parse("1.0.1", [2025]).Root!, "ProductCode") ?? string.Empty;
            string upgradeA = Attr(Parse("1.0.0", [2025]).Root!, "UpgradeCode") ?? string.Empty;
            string upgradeB = Attr(Parse("1.0.1", [2025]).Root!, "UpgradeCode") ?? string.Empty;

            run.True(a.Length > 0 && a != b, "the storefront upgrades by product code, so each version has its own");
            run.True(upgradeA.Length > 0 && upgradeA == upgradeB, "the upgrade code is the same across versions");
        });

        run.Case("the declared version is a storefront-acceptable one: never 0.x (ADR 0018)", () =>
        {
            string declared = DeclaredVersion();

            run.False(declared.StartsWith("0.", StringComparison.Ordinal), $"declared {declared} must not start with 0.");
            run.True(Version.TryParse(declared, out _), $"declared {declared} is a dotted version");
        });

        return run.Report("AutodeskBundleManifest");
    }

    /// <summary>The version this build declares: the Core assembly's, minus the commit metadata.</summary>
    internal static string DeclaredVersion()
        => BuildIdentity.Parse(
            typeof(AutodeskBundleManifest).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion).Version;

    private static XDocument Parse(string version, int[] years)
        => XDocument.Parse(AutodeskBundleManifest.Build(version, years));

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);
}
