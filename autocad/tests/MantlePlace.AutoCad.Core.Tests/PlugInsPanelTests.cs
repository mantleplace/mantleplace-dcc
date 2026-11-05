using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The Add-ins tab panel: which commands it carries, and which of the ribbon's tabs it goes on. The
/// shell builds the panel and reads the ribbon's tabs; what can be pinned here is the content and the
/// placement decision, over the tab identifiers a live Civil 3D 2027 and a live AutoCAD 2027 showed.
/// </summary>
internal static class PlugInsPanelTests
{
    // The tabs Civil 3D 2027 shows past its own, as read live on 2026-11-04 (autocad/docs/api-record.md).
    private static readonly string[] Civil3dTabs =
        ["CIVIL.ID_Civil3DHome", "CIVIL.ID_Civil3DOutput", "CIVIL.RBN_08112144", "CIVIL.ID_Civil3DAddins", "EXPRESS.RBN_0047", "FEATUREDAPPS.RBN_190_37149"];

    // And plain AutoCAD 2027's.
    private static readonly string[] AutoCadTabs =
        ["ACAD.ID_TabHome", "ACAD.ID_TabOutput", "ACAD.RBN_00012112", "ACAD.ID_TabCollaborate", "EXPRESS.RBN_0047", "FEATUREDAPPS.RBN_190_37149"];

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

        run.Case("each command has a label, a tooltip and a help topic of its own", () =>
        {
            foreach (PanelCommand command in PlugInsPanel.Commands)
            {
                run.True(command.Label.Length > 0, $"{command.Command} is labelled");
                run.True(command.Tooltip.Length > 0, $"{command.Command} has a tooltip");
                run.Equal(command.HelpTopic, command.Key.ToLowerInvariant(), $"F1 over {command.Command} names its own topic");
            }

            run.Equal(
                PlugInsPanel.Commands.Select(command => command.HelpTopic).Distinct(StringComparer.Ordinal).Count(),
                PlugInsPanel.Commands.Count,
                "no two buttons share a topic");
        });

        run.Case("the help page is the AutoCAD plugin's own, in the public repository", () =>
        {
            run.True(
                AutoCadHelp.PageUrl.StartsWith("https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/", StringComparison.Ordinal),
                "the AutoCAD folder's page");
            run.True(AutoCadHelp.PageUrl != HelpLinks.RibbonHelpUrl, "not Revit's");
        });

        run.Case("in Civil 3D the panel joins Civil 3D's own Add-ins tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(Civil3dTabs));

            run.Equal(placement?.TabId, "CIVIL.ID_Civil3DAddins", "the Add-ins tab of Civil 3D's main customization file");
            run.True(placement?.HostTab == true, "a tab the host made, not a second one");
        });

        run.Case("in plain AutoCAD the panel joins AutoCAD's own Add-ins tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(AutoCadTabs));

            run.Equal(placement?.TabId, "ACAD.RBN_00012112", "the Add-ins tab of AutoCAD's main customization file");
            run.True(placement?.HostTab == true, "a tab the host made");
        });

        run.Case("a workspace that shows both hosts' Add-ins tabs gets the panel on Civil 3D's, once", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen([.. AutoCadTabs, .. Civil3dTabs]));

            run.Equal(placement?.TabId, "CIVIL.ID_Civil3DAddins", "Civil 3D's tab is the one Civil 3D's workspaces show");
        });

        run.Case("a panel already on the ribbon is not added again", () =>
        {
            List<RibbonTabSeen> tabs = Seen(Civil3dTabs);
            tabs[3] = tabs[3] with { PanelIds = ["RBN_190_B55E9", PlugInsPanel.PanelId] };

            run.True(PlugInsPanel.Place(tabs) is null, "a workspace reload or a second idle call places nothing");

            List<RibbonTabSeen> own = [.. Seen(["ACAD.ID_TabHome"]), new(PlugInsPanel.OwnTabId, [PlugInsPanel.PanelId])];
            run.True(PlugInsPanel.Place(own) is null, "nor when it is on the plugin's own tab");
        });

        run.Case("with no host Add-ins tab on the ribbon the panel gets a tab of its own, so the commands stay reachable", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(["ACAD.ID_TabHome", "EXPRESS.RBN_0047"]));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.HostTab == false, "the shell has to make it");
        });

        run.Case("a tab is the host's by its identifier, never by its title", () =>
        {
            // The partial customization file this replaced gave its own tab the AutoCAD tab's UID; the
            // host read it as MANTLEPLACE.RBN_00012112, a second "Add-ins" tab. That one is not the host's.
            PanelPlacement? placement = PlugInsPanel.Place(Seen(["ACAD.ID_TabHome", "MANTLEPLACE.RBN_00012112"]));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "a group's tab that shares the UID is not AutoCAD's");
            run.Equal(
                string.Join(",", PlugInsPanel.HostTabIds),
                "CIVIL.ID_Civil3DAddins,ACAD.RBN_00012112",
                "Civil 3D's first, then AutoCAD's");
        });

        run.Case("every identifier the panel adds is the plugin's own, so it cannot clash with another add-in's", () =>
        {
            run.True(PlugInsPanel.PanelId.StartsWith("MANTLEPLACE", StringComparison.Ordinal), PlugInsPanel.PanelId);
            run.True(PlugInsPanel.OwnTabId.StartsWith("MANTLEPLACE", StringComparison.Ordinal), PlugInsPanel.OwnTabId);
            foreach (PanelCommand command in PlugInsPanel.Commands)
            {
                run.True(command.ButtonId.StartsWith("MANTLEPLACE_", StringComparison.Ordinal), command.ButtonId);
            }
        });

        run.Case("the only image is the monogram, at the two sizes a ribbon button uses", () =>
            run.Equal(string.Join(",", PlugInsPanel.ImageSources), "MantlePlaceMark_16.png,MantlePlaceMark_32.png", "the monogram's 16 and 32 renders"));

        return run.Report("Add-ins panel");
    }

    private static List<RibbonTabSeen> Seen(IEnumerable<string> ids) => [.. ids.Select(id => new RibbonTabSeen(id, []))];
}
