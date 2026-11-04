using System.Windows;
using MantlePlace.PlatformApi;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The update prompt: the platform answered 426 <c>client_update_required</c>, so this build is older
/// than mantle.place serves. Shown once per Revit session, on Revit's thread, with the download link.
/// </summary>
/// <remarks>
/// <para>
/// Once, because every call the platform refuses reports it: a vault listing, a renewal and a
/// connected host's poll can all arrive within a second. The refused call still fails on its own
/// channel with the same sentence (<see cref="ClientUpdatePrompt"/>), so the vault window's line says
/// it too.
/// </para>
/// <para>
/// A WPF message box rather than a <c>TaskDialog</c>: the notice can arrive from any call, outside a
/// Revit API context, where a <c>TaskDialog</c> is not allowed.
/// </para>
/// </remarks>
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
