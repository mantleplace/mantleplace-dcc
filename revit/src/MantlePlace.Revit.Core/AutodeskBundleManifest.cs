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
/// Pure: the version and the years in, the text out. The package script asks for it, writes it
/// beside <c>Contents</c> and compares the version it carries with the one the build declares, so a
/// stale build cannot ship under a new number.
/// </para>
/// <para>
/// One build serves every year (<c>revit/CLAUDE.md</c>), so every year's component entry names the
/// same module, the <c>.addin</c> inside <c>Contents</c>. The entries are still one per year because
/// the bundle format selects by <c>RuntimeRequirements</c>, and a year that is not declared is a year
/// the storefront will not install into.
/// </para>
/// </remarks>
public static class AutodeskBundleManifest
{
    /// <summary>The root folder of the release zip, and the folder name Autodesk loads as a bundle.</summary>
    public const string BundleFolderName = "MantlePlace.bundle";

    /// <summary>The manifest's file name, beside the <c>Contents</c> folder.</summary>
    public const string FileName = "PackageContents.xml";

    /// <summary>The name the bundle and its component carry.</summary>
    public const string AppName = "Mantle Place";

    /// <summary>The monogram render the package script copies in, by its name in the add-in's Resources.</summary>
    public const string IconSourceFileName = "MantlePlaceMark_48.png";

    /// <summary>Where the monogram sits inside the bundle, relative to its root.</summary>
    public const string IconBundlePath = "./Contents/Resources/" + IconSourceFileName;

    private const string AddinModulePath = "./Contents/MantlePlace.addin";
    private const string Vendor = "Mantle Place";
    private const string VendorUrl = "https://mantle.place";
    private const string Description = "Imports a Mantle Place bundle into Revit: terrain, site model, imagery and georeference.";

    // Constant across versions: the storefront recognises an upgrade of the same product by it.
    private const string UpgradeCode = "{2D6E8F10-4B7A-4C5E-9F31-7A0C5B8E4D62}";

    /// <summary>The Revit years the plugin supports, oldest first. The one list the manifest is built from.</summary>
    public static IReadOnlyList<int> SupportedRevitYears { get; } = [2025, 2026, 2027];

    /// <summary>The module Revit <paramref name="year"/> loads, relative to the bundle root.</summary>
    public static string ModulePathFor(int year) => AddinModulePath;

    /// <summary>Builds the manifest text for <paramref name="version"/> across <paramref name="years"/>.</summary>
    /// <exception cref="ArgumentException">A blank version, no years, or a repeated year.</exception>
    public static string Build(string version, IReadOnlyList<int> years)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(years);

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A bundle manifest needs the version it carries.", nameof(version));
        }

        if (years.Count == 0)
        {
            throw new ArgumentException("A bundle manifest needs at least one Revit year.", nameof(years));
        }

        if (years.Distinct().Count() != years.Count)
        {
            throw new ArgumentException("A Revit year may appear once.", nameof(years));
        }

        XElement components = new("Components");
        foreach (int year in years)
        {
            string series = "R" + year.ToString(CultureInfo.InvariantCulture);
            components.Add(
                new XElement(
                    "RuntimeRequirements",
                    new XAttribute("OS", "Win64"),
                    new XAttribute("Platform", "Revit"),
                    new XAttribute("SeriesMin", series),
                    new XAttribute("SeriesMax", series)),
                new XElement(
                    "ComponentEntry",
                    new XAttribute("AppName", AppName),
                    new XAttribute("Version", version),
                    new XAttribute("ModuleName", ModulePathFor(year)),
                    new XAttribute("AppDescription", Description),
                    new XAttribute("LoadOnCommandInvocation", "False"),
                    new XAttribute("LoadOnRevitStartup", "True")));
        }

        XElement root = new(
            "ApplicationPackage",
            new XAttribute("SchemaVersion", "1.0"),
            new XAttribute("AutodeskProduct", "Revit"),
            new XAttribute("ProductType", "Application"),
            new XAttribute("Name", AppName),
            new XAttribute("AppVersion", version),
            new XAttribute("Description", Description),
            new XAttribute("Icon", IconBundlePath),
            new XAttribute("ProductCode", ProductCodeFor(version)),
            new XAttribute("UpgradeCode", UpgradeCode),
            new XElement(
                "CompanyDetails",
                new XAttribute("Name", Vendor),
                new XAttribute("Url", VendorUrl)),
            components);

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString() + "\n";
    }

    // One product code per version, derived rather than drawn, so the same version always builds the
    // same bytes and a rebuild of a release cannot present itself to the storefront as a new product.
    private static string ProductCodeFor(string version)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("mantleplace-revit-bundle-" + version));
        return new Guid(hash.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
    }
}
