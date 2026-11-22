using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Ribbon;
using Autodesk.Windows;
using MantlePlace.Wpf;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MantlePlace.AutoCad.Addin;

internal static class PluginRibbon
{
    private static RibbonPaletteSet? _watched;

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
#pragma warning disable CA1031
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

                Text = command.Label.Replace(' ', '\n'),
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                Image = MarkImages.Decode(PlugInsPanel.ImageSources[0]),
                LargeImage = MarkImages.Decode(PlugInsPanel.ImageSources[1]),
                CommandParameter = command.Command,
                CommandHandler = RunCommand.Instance,

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

            document.SendStringToExecute("\x03\x03_" + command + " ", true, false, true);
        }
    }
}
