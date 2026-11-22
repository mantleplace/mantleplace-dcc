using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MantlePlace.Core;

public static class AutodeskBundleManifest
{
    public const string BundleFolderName = "MantlePlace.bundle";

    public const string FileName = "PackageContents.xml";

    public const string AppName = "Mantle Place";

    public const string AutoCadBundleFolderName = "MantlePlaceAutoCad.bundle";

    public const string AutoCadAppName = "Mantle Place for AutoCAD";

    public const string AutoCadNet8Folder = "net8";

    public const string AutoCadNet10Folder = "net10";

    public const string AutoCadModuleFileName = "MantlePlace.AutoCad.Addin.dll";

    public const string IconSourceFileName = "MantlePlaceMark_48.png";

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

        UpgradeCode: "{2D6E8F10-4B7A-4C5E-9F31-7A0C5B8E4D62}",
        ProductCodeSeed: "mantleplace-revit-bundle-",
        Years: [2025, 2026, 2027]);

    private static readonly HostProfile AutoCadProfile = new(
        AutodeskProduct: "AutoCAD",

        Platform: "AutoCAD|Civil3D",
        AppName: AutoCadAppName,
        BundleFolderName: AutoCadBundleFolderName,
        Description: "Brings a Mantle Place bundle into AutoCAD and Civil 3D.",
        UpgradeCode: "{7C3A91E4-5D28-4F6B-A0B7-3E94D12C8F55}",
        ProductCodeSeed: "mantleplace-autocad-bundle-",
        Years: [2025, 2026, 2027]);

    public static IReadOnlyList<int> SupportedRevitYears { get; } = RevitProfile.Years;

    public static IReadOnlyList<int> SupportedYearsFor(BundleHost host) => ProfileOf(host).Years;

    public static string BundleFolderNameFor(BundleHost host) => ProfileOf(host).BundleFolderName;

    public static string AppNameFor(BundleHost host) => ProfileOf(host).AppName;

    public static string ModulePathFor(int year) => ModulePathFor(BundleHost.Revit, year);

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

        List<XElement> groups = [];
        foreach (int year in years)
        {
            string series = SeriesFor(host, year);
            groups.Add(
                new XElement(
                    "Components",
                    new XElement(
                        "RuntimeRequirements",
                        new XAttribute("OS", "Win64"),
                        new XAttribute("Platform", profile.Platform),
                        new XAttribute("SeriesMin", series),
                        new XAttribute("SeriesMax", series)),
                    ComponentEntryFor(profile, host, year, version)));
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
            groups);

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

    private static HostProfile ProfileOf(BundleHost host) => host switch
    {
        BundleHost.Revit => RevitProfile,
        BundleHost.AutoCad => AutoCadProfile,
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown bundle host."),
    };

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
