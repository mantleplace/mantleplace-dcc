using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The Mantle Place panel: which commands it carries, and which of the ribbon's tabs it goes on (a
/// Mantle Place tab of the plugin's own, <c>HPS-57</c>). The shell builds the panel and reads the
/// ribbon's tabs; what can be pinned here is the content and the placement decision, over the tab
/// identifiers a live Civil 3D 2027 and a live AutoCAD 2027 showed.
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
                "MANTLEPLACESIGNIN,MANTLEPLACESIGNOUT,MANTLEPLACEVAULT,MANTLEPLACEABOUT,MANTLEPLACEFEEDBACK",
                "the commands, in HPS-51's order");
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

        run.Case("in Civil 3D the panel goes on a Mantle Place tab of the plugin's own, not on the Add-ins tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(Civil3dTabs));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.MakeTab == true, "the shell makes it, once");
            run.True(placement?.TabId != "CIVIL.ID_Civil3DAddins", "Civil 3D's Add-ins tab is left as it is");
        });

        run.Case("in plain AutoCAD the panel goes on a Mantle Place tab of the plugin's own, not on the Add-ins tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(AutoCadTabs));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.MakeTab == true, "the shell makes it, once");
            run.True(placement?.TabId != "ACAD.RBN_00012112", "AutoCAD's Add-ins tab is left as it is");
        });

        run.Case("a workspace that shows both hosts' Add-ins tabs still gets one tab of the plugin's own", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen([.. AutoCadTabs, .. Civil3dTabs]));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.MakeTab == true, "made, not borrowed");
        });

        run.Case("a ribbon with no tabs of the host's at all still gets the plugin's tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place(Seen(["ACAD.ID_TabHome", "EXPRESS.RBN_0047"]));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.MakeTab == true, "the shell has to make it");
        });

        run.Case("a panel already on the ribbon is not added again", () =>
        {
            List<RibbonTabSeen> own = [.. Seen(Civil3dTabs), new(PlugInsPanel.OwnTabId, [PlugInsPanel.PanelId])];
            run.True(PlugInsPanel.Place(own) is null, "a workspace reload or a second idle call places nothing");

            List<RibbonTabSeen> tabs = Seen(Civil3dTabs);
            tabs[3] = tabs[3] with { PanelIds = ["RBN_190_B55E9", PlugInsPanel.PanelId] };
            run.True(PlugInsPanel.Place(tabs) is null, "nor when it is found on some other tab");
        });

        run.Case("a second WorkspaceLoaded adds no second tab or panel", () =>
        {
            List<RibbonTabSeen> ribbon = Seen(Civil3dTabs);

            // What the shell does with each answer: make the tab if told to, then add the panel.
            int Press()
            {
                if (PlugInsPanel.Place(ribbon) is not { } placement)
                {
                    return 0;
                }

                if (placement.MakeTab)
                {
                    ribbon.Add(new RibbonTabSeen(placement.TabId, []));
                }

                int at = ribbon.FindIndex(tab => tab.Id == placement.TabId);
                ribbon[at] = ribbon[at] with { PanelIds = [.. ribbon[at].PanelIds, PlugInsPanel.PanelId] };
                return 1;
            }

            run.Equal(Press(), 1, "the first press adds the panel");
            run.Equal(Press(), 0, "the second adds nothing");
            run.Equal(ribbon.Count(tab => tab.Id == PlugInsPanel.OwnTabId), 1, "one Mantle Place tab");
            run.Equal(ribbon.Sum(tab => tab.PanelIds.Count(id => id == PlugInsPanel.PanelId)), 1, "one Mantle Place panel");
            run.Equal(ribbon.Count, Civil3dTabs.Length + 1, "the host's tabs are as they were, plus ours");
        });

        run.Case("a host that rebuilt its ribbon and kept the tab gets the panel on it, without a second tab", () =>
        {
            PanelPlacement? placement = PlugInsPanel.Place([.. Seen(AutoCadTabs), new(PlugInsPanel.OwnTabId, [])]);

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "the plugin's tab");
            run.True(placement?.MakeTab == false, "already there");
        });

        run.Case("a tab is the plugin's by its identifier, never by its title", () =>
        {
            // The partial customization file this replaced made a tab the host read as
            // MANTLEPLACE.RBN_00012112, a second "Add-ins" tab. That one is not ours.
            PanelPlacement? placement = PlugInsPanel.Place(Seen(["ACAD.ID_TabHome", "MANTLEPLACE.RBN_00012112"]));

            run.Equal(placement?.TabId, PlugInsPanel.OwnTabId, "a group's tab that shares the UID is not the plugin's");
            run.True(placement?.MakeTab == true, "so the plugin's own is made");
        });

        run.Case("the tab and the panel read Mantle Place, HPS-51's words for the plugin", () =>
        {
            run.Equal(PlugInsPanel.TabText, "Mantle Place", "the tab's title");
            run.Equal(PlugInsPanel.PanelText, "Mantle Place", "the panel's title");
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

        return run.Report("Mantle Place panel");
    }

    private static List<RibbonTabSeen> Seen(IEnumerable<string> ids) => [.. ids.Select(id => new RibbonTabSeen(id, []))];
}
