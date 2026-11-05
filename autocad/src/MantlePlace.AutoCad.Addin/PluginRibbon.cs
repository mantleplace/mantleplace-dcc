using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Ribbon;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Puts the Mantle Place panel on a Mantle Place tab of the plugin's own, and puts it back each time the
/// host rebuilds its ribbon.
/// </summary>
/// <remarks>
/// <para>
/// Built in code because a partial CUIX file cannot do it: its tab is its own customization group's, so
/// AutoCAD and Civil 3D 2027 showed it as a second "Add-ins" tab beside their own
/// (<c>autocad/docs/api-record.md</c>). Which tab, whether it must be made and whether the panel is
/// already there is <see cref="PlugInsPanel.Place"/> in the pure core; this reads the ribbon's tabs,
/// makes the tab when told to, and adds the panel.
/// </para>
/// <para>
/// The host builds its ribbon from its customization files at startup, after this assembly
/// initialises, and again on every workspace change and customization reload, which drops a panel
/// added in code. So the panel is placed on the first idle that finds the ribbon, and again on each
/// <c>WorkspaceLoaded</c>; placing it twice is a no-op.
/// </para>
/// </remarks>
internal static class PluginRibbon
{
    private static RibbonPaletteSet? _watched;

    /// <summary>Starts watching for the ribbon. AutoCAD's UI thread, from <c>Initialize</c>.</summary>
    internal static void Attach()
    {
        AcApplication.Idle += OnIdle;
        if (RibbonServices.RibbonPaletteSet is { } set)
        {
            Watch(set);
        }
        else
        {
            RibbonServices.RibbonPaletteSetCreated += OnPaletteSetCreated;
        }
    }

    /// <summary>Stops watching. AutoCAD's UI thread, from <c>Terminate</c>.</summary>
    internal static void Detach()
    {
        AcApplication.Idle -= OnIdle;
        RibbonServices.RibbonPaletteSetCreated -= OnPaletteSetCreated;
        if (_watched is not null)
        {
            _watched.WorkspaceLoaded -= OnWorkspaceLoaded;
            _watched = null;
        }
    }

    private static void OnPaletteSetCreated(object? sender, EventArgs e)
    {
        RibbonServices.RibbonPaletteSetCreated -= OnPaletteSetCreated;
        if (RibbonServices.RibbonPaletteSet is { } set)
        {
            Watch(set);
        }
    }

    private static void Watch(RibbonPaletteSet set)
    {
        _watched = set;
        set.WorkspaceLoaded += OnWorkspaceLoaded;
    }

    private static void OnWorkspaceLoaded(object? sender, EventArgs e) => Place();

    // Startup: keeps asking on each idle until the host has built a ribbon with tabs on it, then stops.
    private static void OnIdle(object? sender, EventArgs e)
    {
        if (ComponentManager.Ribbon is not { Tabs.Count: > 0 })
        {
            return;
        }

        AcApplication.Idle -= OnIdle;
        Place();
    }

    private static void Place()
    {
        try
        {
            if (ComponentManager.Ribbon is not { } ribbon)
            {
                return;
            }

            List<RibbonTabSeen> seen = [.. ribbon.Tabs.Select(tab => new RibbonTabSeen(
                tab.Id ?? string.Empty,
                [.. tab.Panels.Select(panel => panel.Source?.Id ?? string.Empty)]))];
            if (PlugInsPanel.Place(seen) is not { } placement)
            {
                return;
            }

            RibbonTab? tab = placement.MakeTab ? null : ribbon.Tabs.FirstOrDefault(candidate => string.Equals(candidate.Id, placement.TabId, StringComparison.Ordinal));
            if (tab is null)
            {
                tab = new RibbonTab { Id = PlugInsPanel.OwnTabId, Title = PlugInsPanel.TabText, Name = PlugInsPanel.TabText };
                ribbon.Tabs.Add(tab);
            }

            tab.Panels.Add(BuildPanel());
        }
#pragma warning disable CA1031 // A ribbon that will not take the panel is said, never left to take AutoCAD down; the commands still type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MantlePlaceApplication.Say($"The Mantle Place panel could not be added to the ribbon ({ex.Message}). Its commands can still be typed.");
        }
    }

    private static RibbonPanel BuildPanel()
    {
        RibbonPanelSource source = new() { Id = PlugInsPanel.PanelId, Title = PlugInsPanel.PanelText, Name = PlugInsPanel.PanelText };
        Uri help = new(AutoCadHelp.PageUrl);
        foreach (PanelCommand command in PlugInsPanel.Commands)
        {
            source.Items.Add(new RibbonButton
            {
                Id = command.ButtonId,

                // Large buttons break their text where it is told to, as the host's own do.
                Text = command.Label.Replace(' ', '\n'),
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                Image = ResourceImages.Decode(PlugInsPanel.ImageSources[0]),
                LargeImage = ResourceImages.Decode(PlugInsPanel.ImageSources[1]),
                CommandParameter = command.Command,
                CommandHandler = RunCommand.Instance,

                // F1 over the button: the host opens the help source at the topic.
                ToolTip = new RibbonToolTip
                {
                    Title = command.Label,
                    Content = command.Tooltip,
                    Command = command.Command,
                    HelpSource = help,
                    HelpTopic = command.HelpTopic,
                    IsHelpEnabled = true,
                },
            });
        }

        return new RibbonPanel { Source = source };
    }

    /// <summary>Types a button's command into the active drawing, as the host's own buttons do.</summary>
    private sealed class RunCommand : ICommand
    {
        internal static readonly RunCommand Instance = new();

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            string? command = parameter switch
            {
                RibbonCommandItem item => item.CommandParameter as string,
                string text => text,
                _ => null,
            };
            if (command is null || AcApplication.DocumentManager.MdiActiveDocument is not Document document)
            {
                return;
            }

            // Two escapes end whatever command is running, as a ribbon macro's ^C^C does.
            document.SendStringToExecute("\x03\x03_" + command + " ", true, false, true);
        }
    }
}
