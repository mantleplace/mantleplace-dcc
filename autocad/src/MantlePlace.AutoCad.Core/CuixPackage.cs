using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MantlePlace.AutoCad;

/// <summary>
/// The plugin's partial CUIX file: the customization parts AutoCAD reads, written from
/// <see cref="PlugInsPanel"/>, and the archive they travel in.
/// </summary>
/// <remarks>
/// <para>
/// Pure: parts in, bytes out. A CUIX file is an Open Packaging archive of XML parts, the shape the
/// shipped customization files (<c>acad.cuix</c> and the partials beside it) have, so nothing binary
/// is stored in the repository. The package script writes the file into the bundle and the bundle
/// manifest names it, one entry per supported year.
/// </para>
/// <para>
/// What only the host can say is whether it accepts the file: the load, the merge into the
/// Plug-Ins tab, and what F1 does over a button are release checks (<c>autocad/README.md</c>). What
/// this pins is the content those checks load.
/// </para>
/// </remarks>
public static class CuixPackage
{
    private const string Revision = "18";
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    /// <summary>
    /// The XML parts by their name in the archive, in the order they are written. The archive adds
    /// the package parts and the images (<see cref="Build"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parts() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Header.cui"] = Header(),
        ["MenuGroup.cui"] = MenuGroup(),
        ["RibbonRoot.cui"] = RibbonRoot(),
    };

    /// <summary>The archive's bytes: every part, the package index and the monogram images.</summary>
    /// <param name="images">The monogram renders by file name; every name in <see cref="PlugInsPanel.ImageSources"/>.</param>
    /// <exception cref="ArgumentException">An image the panel names is missing.</exception>
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        foreach (string name in PlugInsPanel.ImageSources)
        {
            if (!images.ContainsKey(name))
            {
                throw new ArgumentException($"The panel names {name} and none was given.", nameof(images));
            }
        }

        IReadOnlyDictionary<string, string> parts = Parts();

        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in parts)
            {
                Write(archive, name, Encoding.UTF8.GetBytes(text));
            }

            foreach (string name in PlugInsPanel.ImageSources)
            {
                Write(archive, name, images[name]);
            }

            Write(archive, "Menu_Package_Info.xml", Utf8WithMark(PackageInfo(parts.Keys)));
            Write(archive, "[Content_Types].xml", Utf8WithMark(ContentTypes()));
            Write(archive, "_rels/.rels", Utf8WithMark(Relationships(parts.Keys)));
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        // A fixed timestamp, so the same input is the same bytes.
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream target = entry.Open();
        target.Write(bytes, 0, bytes.Length);
    }

    private static byte[] Utf8WithMark(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private static string Header() => Document(
        new XElement(
            "CustSection",
            new XElement("FileVersion", new XAttribute("MajorVersion", "0"), new XAttribute("MinorVersion", "6"), new XAttribute("IncrementalVersion", "1"), new XAttribute("UserVersion", "0")),
            new XElement("Header", new XElement("CommonConfiguration", new XElement("CommonItems", Rev("ModifiedRev"))))));

    private static string MenuGroup()
    {
        XElement group = new("MacroGroup", new XAttribute("Name", PlugInsPanel.GroupName + "Macros"), new XAttribute("Citizen", "A"));
        foreach (PanelCommand command in PlugInsPanel.Commands)
        {
            group.Add(new XElement(
                "MenuMacro",
                new XAttribute("UID", command.MacroId),
                new XElement(
                    "Macro",
                    new XAttribute("type", "Any"),
                    Rev("Revision"),
                    Rev("ModifiedRev"),
                    new XElement("Name", command.Label),
                    new XElement("Command", "^C^C" + command.Command + " "),
                    new XElement("HelpString", command.Tooltip),
                    new XElement("SmallImage", new XAttribute("Name", PlugInsPanel.ImageSources[0])),
                    new XElement("LargeImage", new XAttribute("Name", PlugInsPanel.ImageSources[1])),
                    new XElement("CLICommand", command.Command),

                    // F1 over the button: the host reads the help source and topic off the tooltip.
                    new XElement("ToolTip", new XAttribute("HelpSource", AutoCadHelp.PageUrl), new XAttribute("HelpTopic", command.Key.ToLowerInvariant())))));
        }

        return Document(new XElement(
            "MenuGroup",
            new XAttribute("Name", PlugInsPanel.GroupName),
            new XAttribute("DisplayName", PlugInsPanel.GroupName),
            group));
    }

    private static string RibbonRoot()
    {
        const string panelId = "MANTLEPLACE_PANEL";

        XElement row = new("RibbonRow", new XAttribute("UID", "MANTLEPLACE_ROW"), Rev("ModifiedRev"));
        foreach (PanelCommand command in PlugInsPanel.Commands)
        {
            row.Add(new XElement(
                "RibbonCommandButton",
                new XAttribute("UID", command.ButtonId),
                new XAttribute("Id", "AcRibbonCommandButton"),
                new XAttribute("Text", command.Label.Replace(' ', '\r')),
                new XAttribute("ButtonStyle", "LargeWithText"),
                new XAttribute("MenuMacroID", command.MacroId),
                new XElement("TooltipTitle", new XAttribute("xlate", "true"), new XAttribute("UID", "MANTLEPLACE_TIP_" + command.Key), command.Label),
                Rev("ModifiedRev")));
        }

        XElement panel = new(
            "RibbonPanelSource",
            new XAttribute("UID", panelId),
            new XAttribute("Text", PlugInsPanel.PanelText),
            new XAttribute("HiddenInEditor", "false"),
            Rev("ModifiedRev"),
            new XElement("Name", new XAttribute("xlate", "true"), new XAttribute("UID", "MANTLEPLACE_PANEL_NAME"), PlugInsPanel.PanelText),
            row);

        // The tab is the host's own, named by its identifier: AutoCAD merges a partial's tab with the
        // main file's tab of the same identifier, so this panel joins the Plug-Ins tab's.
        XElement tab = new(
            "RibbonTabSource",
            new XAttribute("Text", PlugInsPanel.TabText),
            new XAttribute("UID", PlugInsPanel.TabId),
            Rev("ModifiedRev"),
            new XElement(
                "RibbonPanelSourceReference",
                new XAttribute("UID", "MANTLEPLACE_PANELREF"),
                new XAttribute("PanelId", panelId),
                new XAttribute("ResizeStyle", "Default"),
                Rev("ModifiedRev")));

        return Document(new XElement(
            "RibbonRoot",
            new XElement("RibbonPanelSourceCollection", panel),
            new XElement("RibbonTabSourceCollection", tab)));
    }

    private static string PackageInfo(IEnumerable<string> parts)
    {
        XElement root = new("MenuPackageParts");
        foreach (string part in parts)
        {
            root.Add(new XElement("PartData", new XAttribute("PartData_Name", "/" + part), new XAttribute("PartData_Modified", Stamp)));
        }

        root.Add(new XElement("PartData", new XAttribute("PartData_Name", "/VirtualMNRRoot"), new XAttribute("PartData_Modified", Stamp)));
        root.Add(new XElement("PartData", new XAttribute("PartData_Name", "/Menu_Package_Info.xml"), new XAttribute("PartData_Modified", Stamp)));
        return Document(root);
    }

    private static string ContentTypes()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        return Document(new XElement(
            ns + "Types",
            new XElement(ns + "Default", new XAttribute("Extension", "cui"), new XAttribute("ContentType", "text/xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "text/xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")),
            new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml"))));
    }

    private static string Relationships(IEnumerable<string> parts)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        XElement root = new(ns + "Relationships");
        int index = 0;
        foreach (string part in parts)
        {
            index++;
            root.Add(new XElement(
                ns + "Relationship",
                new XAttribute("Type", "CUI"),
                new XAttribute("Target", "/" + part),
                new XAttribute("Id", "Rmantleplace" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))));
        }

        return Document(root);
    }

    private static XElement Rev(string name)
        => new(name, new XAttribute("MajorVersion", Revision), new XAttribute("MinorVersion", "0"), new XAttribute("UserVersion", "0"));

    private static string Document(XElement root)
        => "<?xml version=\"1.0\"?>\n" + root.ToString(SaveOptions.None);
}
