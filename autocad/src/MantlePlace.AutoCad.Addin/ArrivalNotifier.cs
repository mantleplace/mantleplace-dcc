using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

internal static class ArrivalNotifier
{
    private static readonly List<NoticePopup> Shown = [];

    internal static void OnArrived(IReadOnlyList<VaultBundle> arrivals, bool vaultOpen)
    {
        if (VaultNews.NoticeFor(arrivals, vaultOpen) is not { } notice)
        {
            return;
        }

        Show(notice.SelectOrderId, notice.Text);
    }

    internal static void Seen()
    {
        foreach (NoticePopup popup in Shown.ToList())
        {
            popup.Close();
        }
    }

    internal static void Forget()
    {
        Seen();
        Shown.Clear();
    }

    private static void Show(string? selectOrderId, string text)
    {
        IntPtr hostWindow = MantlePlaceApplication.MainWindowHandle();

        if (hostWindow == IntPtr.Zero || HostWindowGeometry.IsMinimised(hostWindow))
        {
            return;
        }

        if (selectOrderId is not null)
        {
            foreach (NoticePopup stale in Shown.Where(popup => popup.SelectOrderId == selectOrderId).ToList())
            {
                stale.Close();
            }
        }

        while (Shown.Count >= NoticeStack.MaxShown)
        {
            Shown[^1].Close();
        }

        NoticePopup shown = new(selectOrderId, text, hostWindow);
        shown.Chosen += (_, _) => VaultBrowser.Open(hostWindow, selectOrderId);
        shown.Closed += (_, _) =>
        {
            Shown.Remove(shown);
            Restack(hostWindow);
        };

        Shown.Insert(0, shown);
        Restack(hostWindow);
        shown.Show();
    }

    private static void Restack(IntPtr hostWindow)
    {
        if (HostWindowGeometry.Bounds(hostWindow) is not { } owner)
        {
            return;
        }

        for (int index = 0; index < Shown.Count; index++)
        {
            Shown[index].PlaceAt(NoticeStack.Place(owner, index));
        }
    }
}

internal static class VaultBrowser
{
    private static VaultBrowserWindow? _window;

    internal static bool IsOpen => _window is not null;

    internal static void Open(IntPtr hostWindow, string? selectOrderId)
    {
        ArrivalNotifier.Seen();

        if (_window is not null)
        {
            if (selectOrderId is not null)
            {
                _window.Select(selectOrderId);
            }

            _window.Activate();
            return;
        }

        _window = new VaultBrowserWindow(
            MantlePlaceApplication.Session,
            MantlePlaceApplication.Vault,
            MantlePlaceApplication.Cache,
            MantlePlaceApplication.Announced,
            FeedbackLauncher.TryOpen,

            new VaultImporting(MantlePlaceApplication.Watcher, new AutoCadVaultImport()),
            hostWindow,
            selectOrderId);

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    internal static void Close() => _window?.Close();
}
