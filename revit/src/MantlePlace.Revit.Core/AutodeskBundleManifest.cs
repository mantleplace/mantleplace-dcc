using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The <c>PackageContents.xml</c> that makes the release zip an Autodesk application bundle, so the
/// same archive installs from the Autodesk storefront and by hand (ADR 0018).
/// </summary>
/// <remarks>
/// <para>
/// Pure: the version, the years and the host in, the text out. The package script asks for it,
/// writes it beside <c>Contents</c> and compares the version it carries with the one the build
/// declares, so a stale build cannot ship under a new number.
/// </para>
/// <para>
/// One function serves both Autodesk plugins (<see cref="BundleHost"/>): the AutoCAD tree compiles
/// this file from here by source include, so the two cannot drift into two manifest writers. What
/// the host decides is a <see cref="HostProfile"/>, and nothing else varies.
/// </para>
/// <para>
/// Revit: one build serves every year (<c>revit/CLAUDE.md</c>), so every year's component entry names
/// the same module, the <c>.addin</c> inside <c>Contents</c>. AutoCAD: two builds (.NET 8 for 2025
/// and 2026, .NET 10 for 2027), so each year names the folder of the build it loads. The entries are
/// one per year either way because the bundle format selects by <c>RuntimeRequirements</c>, and a
/// year that is not declared is a year the storefront will not install into.
/// </para>
/// </remarks>
public static class AutodeskBundleManifest
{
    /// <summary>The root folder of the Revit release zip, and the folder name Autodesk loads as a bundle.</summary>
    public const string BundleFolderName = "MantlePlace.bundle";

    /// <summary>The manifest's file name, beside the <c>Contents</c> folder.</summary>
    public const string FileName = "PackageContents.xml";

    /// <summary>The name the Revit bundle and its component carry.</summary>
    public const string AppName = "Mantle Place";

    /// <summary>The root folder of the AutoCAD release zip. It differs from Revit's so both can sit in one Autodesk bundle folder.</summary>
    public const string AutoCadBundleFolderName = "MantlePlaceAutoCad.bundle";

    /// <summary>The name the AutoCAD bundle and its component carry. It differs from Revit's for the same reason.</summary>
    public const string AutoCadAppName = "Mantle Place for AutoCAD";

    /// <summary>The folder under <c>Contents</c> holding the .NET 8 build, which AutoCAD and Civil 3D 2025 and 2026 load.</summary>
    public const string AutoCadNet8Folder = "net8";

    /// <summary>The folder under <c>Contents</c> holding the .NET 10 build, which AutoCAD and Civil 3D 2027 load.</summary>
    public const string AutoCadNet10Folder = "net10";

    /// <summary>The AutoCAD plugin's entry assembly, the one module AutoCAD loads from each build folder.</summary>
    public const string AutoCadModuleFileName = "MantlePlace.AutoCad.Addin.dll";

    /// <summary>The AutoCAD plugin's partial CUIX file, the ribbon panel AutoCAD loads from the bundle, relative to the bundle root.</summary>
    public const string AutoCadCuixModulePath = "./Contents/MantlePlace.cuix";

    /// <summary>The monogram render the package script copies in, by its name in the add-in's Resources.</summary>
    public const string IconSourceFileName = "MantlePlaceMark_48.png";

    /// <summary>Where the monogram sits inside the bundle, relative to its root.</summary>
    public const string IconBundlePath = "./Contents/Resources/" + IconSourceFileName;

    private const string AddinModulePath = "./Contents/MantlePlace.addin";
    private const string Vendor = "Mantle Place";
    private const string VendorUrl = "https://mantle.place";

    private static readonly HostProfile RevitProfile = new(
        AutodeskProduct: "Revit",
        Platform: "Revit",
        AppName: AppName,
        BundleFolderName: BundleFolderName,
        Description: "Imports a Mantle Place bundle into Revit: terrain, site model, imagery and georeference.",

        // Constant across versions: the storefront recognises an upgrade of the same product by it.
        UpgradeCode: "{2D6E8F10-4B7A-4C5E-9F31-7A0C5B8E4D62}",
        ProductCodeSeed: "mantleplace-revit-bundle-",
        Years: [2025, 2026, 2027]);

    private static readonly HostProfile AutoCadProfile = new(
        AutodeskProduct: "AutoCAD",

        // AutoCAD and Civil 3D are one host here, so one requirement names both platforms; the
        // Autodesk-installed bundles that span platforms write the same pipe-separated list.
        Platform: "AutoCAD|Civil3D",
        AppName: AutoCadAppName,
        BundleFolderName: AutoCadBundleFolderName,
        Description: "Brings a Mantle Place bundle into AutoCAD and Civil 3D.",
        UpgradeCode: "{7C3A91E4-5D28-4F6B-A0B7-3E94D12C8F55}",
        ProductCodeSeed: "mantleplace-autocad-bundle-",
        Years: [2025, 2026, 2027]);

    /// <summary>The Revit years the plugin supports, oldest first. The one list the Revit manifest is built from.</summary>
    public static IReadOnlyList<int> SupportedRevitYears { get; } = RevitProfile.Years;

    /// <summary>The years <paramref name="host"/>'s plugin supports, oldest first. The one list its manifest is built from.</summary>
    public static IReadOnlyList<int> SupportedYearsFor(BundleHost host) => ProfileOf(host).Years;

    /// <summary>The root folder of <paramref name="host"/>'s release zip.</summary>
    public static string BundleFolderNameFor(BundleHost host) => ProfileOf(host).BundleFolderName;

    /// <summary>The name <paramref name="host"/>'s bundle carries, which is how a deploy script tells it from another's.</summary>
    public static string AppNameFor(BundleHost host) => ProfileOf(host).AppName;

    /// <summary>The module Revit <paramref name="year"/> loads, relative to the bundle root.</summary>
    public static string ModulePathFor(int year) => ModulePathFor(BundleHost.Revit, year);

    /// <summary>The module <paramref name="host"/>'s <paramref name="year"/> loads, relative to the bundle root.</summary>
    /// <exception cref="ArgumentException">An AutoCAD year the plugin has no series for.</exception>
    public static string ModulePathFor(BundleHost host, int year)
    {
        switch (host)
        {
            case BundleHost.Revit:
                return AddinModulePath;
            case BundleHost.AutoCad:
                _ = SeriesFor(host, year);
                string build = year >= 2027 ? AutoCadNet10Folder : AutoCadNet8Folder;
                return "./Contents/" + build + "/" + AutoCadModuleFileName;
            default:
                throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown bundle host.");
        }
    }

    /// <summary>
    /// The <c>SeriesMin</c> and <c>SeriesMax</c> a year is declared under. Revit's are the year
    /// (<c>R2026</c>); AutoCAD's are the product's own version numbers (2025 is <c>R25.0</c>, 2026 is
    /// <c>R25.1</c>, 2027 is <c>R26.0</c>), which Civil 3D shares. Each year is its own one-point range.
    /// </summary>
    /// <exception cref="ArgumentException">An AutoCAD year the plugin has no series for.</exception>
    public static string SeriesFor(BundleHost host, int year)
    {
        switch (host)
        {
            case BundleHost.Revit:
                return "R" + year.ToString(CultureInfo.InvariantCulture);
            case BundleHost.AutoCad:
                return year switch
                {
                    2025 => "R25.0",
                    2026 => "R25.1",
                    2027 => "R26.0",
                    _ => throw new ArgumentException(
                        "AutoCAD " + year.ToString(CultureInfo.InvariantCulture) + " has no declared series.",
                        nameof(year)),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown bundle host.");
        }
    }

    /// <summary>Builds the manifest text for <paramref name="host"/> at <paramref name="version"/> across <paramref name="years"/>.</summary>
    /// <exception cref="ArgumentException">A blank version, no years, a repeated year, or a year the host has no series for.</exception>
    public static string Build(string version, IReadOnlyList<int> years, BundleHost host)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(years);

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A bundle manifest needs the version it carries.", nameof(version));
        }

        if (years.Count == 0)
        {
            throw new ArgumentException("A bundle manifest needs at least one year.", nameof(years));
        }

        if (years.Distinct().Count() != years.Count)
        {
            throw new ArgumentException("A year may appear once.", nameof(years));
        }

        HostProfile profile = ProfileOf(host);

        XElement components = new("Components");
        foreach (int year in years)
        {
            string series = SeriesFor(host, year);
            components.Add(
                new XElement(
                    "RuntimeRequirements",
                    new XAttribute("OS", "Win64"),
                    new XAttribute("Platform", profile.Platform),
                    new XAttribute("SeriesMin", series),
                    new XAttribute("SeriesMax", series)),
                ComponentEntryFor(profile, host, year, version));

            if (host == BundleHost.AutoCad)
            {
                components.Add(CuixEntryFor(profile, version));
            }
        }

        XElement root = new(
            "ApplicationPackage",
            new XAttribute("SchemaVersion", "1.0"),
            new XAttribute("AutodeskProduct", profile.AutodeskProduct),
            new XAttribute("ProductType", "Application"),
            new XAttribute("Name", profile.AppName),
            new XAttribute("AppVersion", version),
            new XAttribute("Description", profile.Description),
            new XAttribute("Icon", IconBundlePath),
            new XAttribute("ProductCode", ProductCodeFor(profile, version)),
            new XAttribute("UpgradeCode", profile.UpgradeCode),
            new XElement(
                "CompanyDetails",
                new XAttribute("Name", Vendor),
                new XAttribute("Url", VendorUrl)),
            components);

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString() + "\n";
    }

    private static XElement ComponentEntryFor(HostProfile profile, BundleHost host, int year, string version)
    {
        XElement entry = new(
            "ComponentEntry",
            new XAttribute("AppName", profile.AppName),
            new XAttribute("Version", version),
            new XAttribute("ModuleName", ModulePathFor(host, year)),
            new XAttribute("AppDescription", profile.Description),
            new XAttribute("LoadOnCommandInvocation", "False"));

        if (host == BundleHost.AutoCad)
        {
            // A .NET assembly that AutoCAD loads at startup, so the plugin's commands exist before the
            // first one is typed. The Civil 3D part is not named here: the shell loads it, and only
            // when Civil 3D is in the process.
            entry.Add(
                new XAttribute("AppType", ".NET"),
                new XAttribute("LoadOnAutoCADStartup", "True"));
        }
        else
        {
            entry.Add(new XAttribute("LoadOnRevitStartup", "True"));
        }

        return entry;
    }

    // The panel is a customization file, not an assembly: AutoCAD merges a bundle's CUIX into its
    // ribbon on load, so it carries no AppType. It sits under the year's runtime requirement, after
    // the assembly, which is how the format pairs an entry with the release it is for.
    private static XElement CuixEntryFor(HostProfile profile, string version)
        => new(
            "ComponentEntry",
            new XAttribute("AppName", profile.AppName + " ribbon panel"),
            new XAttribute("Version", version),
            new XAttribute("ModuleName", AutoCadCuixModulePath),
            new XAttribute("AppDescription", "The Mantle Place panel on the Plug-Ins tab."),
            new XAttribute("LoadOnCommandInvocation", "False"),
            new XAttribute("LoadOnAutoCADStartup", "True"));

    private static HostProfile ProfileOf(BundleHost host) => host switch
    {
        BundleHost.Revit => RevitProfile,
        BundleHost.AutoCad => AutoCadProfile,
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown bundle host."),
    };

    // One product code per version, derived rather than drawn, so the same version always builds the
    // same bytes and a rebuild of a release cannot present itself to the storefront as a new product.
    private static string ProductCodeFor(HostProfile profile, string version)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile.ProductCodeSeed + version));
        return new Guid(hash.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
    }

    private sealed record HostProfile(
        string AutodeskProduct,
        string Platform,
        string AppName,
        string BundleFolderName,
        string Description,
        string UpgradeCode,
        string ProductCodeSeed,
        IReadOnlyList<int> Years);
}
