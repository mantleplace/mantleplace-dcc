using System.IO.Compression;
using System.Xml.Linq;
using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The Plug-Ins tab panel and the partial CUIX file that carries it. Loading a CUIX is the host's, so
/// what can be pinned here is the file's content: every command is on the panel, each has its help
/// address, nothing but the monogram is an image, and the package is a well-formed archive.
/// </summary>
internal static class PlugInsPanelTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the panel carries every command the plugin registers, once each", () =>
        {
            string[] names = [.. PlugInsPanel.Commands.Select(command => command.Command)];

            run.Equal(
                string.Join(",", names),
                "MANTLEPLACESIGNIN,MANTLEPLACESIGNOUT,MANTLEPLACEVAULT,MANTLEPLACEFEEDBACK,MANTLEPLACEABOUT",
                "the commands, in panel order");
            run.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Length, "none twice");
        });

        run.Case("each command is a macro that runs it, with a name, a tooltip and the help address", () =>
        {
            XElement[] macros = [.. Part("MenuGroup.cui").Descendants("MenuMacro")];

            run.Equal(macros.Length, PlugInsPanel.Commands.Count, "one macro per command");
            foreach (PanelCommand command in PlugInsPanel.Commands)
            {
                XElement macro = macros.Single(candidate => (string?)candidate.Attribute("UID") == command.MacroId).Element("Macro")!;

                run.Equal((string?)macro.Element("Command"), "^C^C" + command.Command + " ", $"{command.Command} runs the command and nothing else");
                run.Equal((string?)macro.Element("Name"), command.Label, $"{command.Command} is named");
                run.True(((string?)macro.Element("HelpString") ?? string.Empty).Length > 0, $"{command.Command} has a tooltip");
                run.Equal(
                    (string?)macro.Element("ToolTip")!.Attribute("HelpSource"),
                    AutoCadHelp.PageUrl,
                    $"F1 over {command.Command} opens the plugin's help page");
            }
        });

        run.Case("the help page is the AutoCAD plugin's own, in the public repository", () =>
        {
            run.True(
                AutoCadHelp.PageUrl.StartsWith("https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/", StringComparison.Ordinal),
                "the AutoCAD folder's page");
            run.True(AutoCadHelp.PageUrl != HelpLinks.RibbonHelpUrl, "not Revit's");
        });

        run.Case("the panel is added to the host's Plug-Ins tab by its identifier, and holds every command", () =>
        {
            XElement ribbon = Part("RibbonRoot.cui");
            XElement tab = ribbon.Descendants("RibbonTabSource").Single();

            run.Equal((string?)tab.Attribute("UID"), PlugInsPanel.TabId, "the tab is the host's, so the partial merges into it");
            run.Equal((string?)tab.Attribute("Text"), PlugInsPanel.TabText, "named as the host names it");

            XElement panel = ribbon.Descendants("RibbonPanelSource").Single();
            run.Equal(
                (string?)tab.Element("RibbonPanelSourceReference")!.Attribute("PanelId"),
                (string?)panel.Attribute("UID"),
                "the tab shows this panel");
            run.Equal((string?)panel.Attribute("Text"), "Mantle Place", "the panel is the plugin's");

            string[] onPanel = [.. panel.Descendants("RibbonCommandButton").Select(button => (string?)button.Attribute("MenuMacroID") ?? string.Empty)];
            run.Equal(
                string.Join(",", onPanel),
                string.Join(",", PlugInsPanel.Commands.Select(command => command.MacroId)),
                "every command, in order");
        });

        run.Case("every identifier in the file is the plugin's own, so it cannot clash with another customization", () =>
        {
            foreach (string name in new[] { "MenuGroup.cui", "RibbonRoot.cui" })
            {
                foreach (XAttribute uid in Part(name).DescendantsAndSelf().Attributes("UID"))
                {
                    bool ours = uid.Value == PlugInsPanel.TabId
                        || uid.Value.StartsWith("MANTLEPLACE_", StringComparison.Ordinal)
                        || uid.Value.StartsWith("ID_MANTLEPLACE_", StringComparison.Ordinal);
                    run.True(ours, $"{name}: {uid.Value}");
                }
            }
        });

        run.Case("the only image is the monogram, at the two sizes a ribbon button uses", () =>
        {
            run.Equal(string.Join(",", PlugInsPanel.ImageSources), "MantlePlaceMark_16.png,MantlePlaceMark_32.png", "the monogram's 16 and 32 renders");
            foreach (XElement macro in Part("MenuGroup.cui").Descendants("Macro"))
            {
                run.Equal((string?)macro.Element("SmallImage")!.Attribute("Name"), "MantlePlaceMark_16.png", "small image");
                run.Equal((string?)macro.Element("LargeImage")!.Attribute("Name"), "MantlePlaceMark_32.png", "large image");
            }
        });

        run.Case("the package is a CUIX archive: the parts it names, and a relationship for each customization part", () =>
        {
            byte[] cuix = CuixPackage.Build(Images(3));

            using ZipArchive archive = new(new MemoryStream(cuix), ZipArchiveMode.Read);
            string[] entries = [.. archive.Entries.Select(entry => entry.FullName)];

            foreach (string expected in new[] { "Header.cui", "MenuGroup.cui", "RibbonRoot.cui", "Menu_Package_Info.xml", "[Content_Types].xml", "_rels/.rels", "MantlePlaceMark_16.png", "MantlePlaceMark_32.png" })
            {
                run.True(entries.Contains(expected), $"holds {expected}");
            }

            using StreamReader rels = new(archive.GetEntry("_rels/.rels")!.Open());
            string[] targets = [.. XElement.Parse(rels.ReadToEnd()).Elements().Select(relationship => (string?)relationship.Attribute("Target") ?? string.Empty)];
            run.True(
                targets.Contains("/MenuGroup.cui") && targets.Contains("/RibbonRoot.cui") && targets.Contains("/Header.cui"),
                "the customization parts are related");
        });

        run.Case("the package refuses to be built without the monogram", () =>
            run.Throws<ArgumentException>(
                () => CuixPackage.Build(new Dictionary<string, byte[]>()),
                "a panel of buttons with no image is a build defect, not a blank icon"));

        run.Case("the same input is the same bytes", () =>
            run.True(CuixPackage.Build(Images(9)).AsSpan().SequenceEqual(CuixPackage.Build(Images(9))), "a rebuild of a release cannot differ"));

        return run.Report("Plug-Ins panel");
    }

    private static Dictionary<string, byte[]> Images(byte fill)
        => PlugInsPanel.ImageSources.ToDictionary(name => name, _ => new[] { fill });

    private static XElement Part(string name) => XElement.Parse(CuixPackage.Parts()[name]);
}
