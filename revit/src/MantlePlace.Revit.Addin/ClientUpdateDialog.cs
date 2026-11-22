using System.Windows;
using MantlePlace.PlatformApi;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class ClientUpdateDialog
{
    private static int _shown;

    internal static void Attach() => PlatformApiCalls.ClientUpdateRequired += OnClientUpdateRequired;

    internal static void Detach() => PlatformApiCalls.ClientUpdateRequired -= OnClientUpdateRequired;

    private static void OnClientUpdateRequired(object? sender, ClientUpdateNotice notice)
    {
        if (Interlocked.Exchange(ref _shown, 1) == 1)
        {
            return;
        }

        MantlePlaceApplication.OnUiThread(() => Show(notice));
    }

    private static void Show(ClientUpdateNotice notice)
    {
        MessageBoxResult choice = MessageBox.Show(
            ClientUpdatePrompt.Sentence(notice.MinimumVersion) + "\n\nOpen the download page now?",
            ClientUpdatePrompt.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice == MessageBoxResult.Yes)
        {
            _ = ShellLauncher.TryOpen(ClientUpdatePrompt.DownloadUrl, out _);
        }
    }
}
