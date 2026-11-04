using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Tells the curator when an order turns up in the vault that nobody asked this plugin to look at: a
/// notice in the corner of AutoCAD's window.
/// </summary>
/// <remarks>
/// <para>
/// The impure half. Whether there is a notice at all, and what it says, is <see cref="VaultNews"/>;
/// where a notice sits is <see cref="NoticeStack"/>; both are in the pure core, where they are asserted
/// without AutoCAD. What is here is windows and Win32 for where AutoCAD's window is.
/// </para>
/// <para>
/// Copied from the Revit plugin's notifier and cut to the notice that needs no Prepare: there is no
/// Prepare-ended notice because the plugin has no Prepare, and no badge because a CUIX button cannot
/// carry one. ⛔ AutoCAD's UI thread only, every member: <see cref="VaultNewsChecker.Arrived"/> is raised
/// on the thread pool and <see cref="MantlePlaceApplication"/> hops before calling in.
/// </para>
/// </remarks>
internal static class ArrivalNotifier
{
    /// <summary>The notices on screen, newest first: index 0 is the bottom of the stack.</summary>
    private static readonly List<NoticePopup> Shown = [];

    /// <summary>A background listing found orders nobody asked for. Tells the curator, if there is anything to tell.</summary>
    internal static void OnArrived(IReadOnlyList<VaultBundle> arrivals, bool vaultOpen)
    {
        if (VaultNews.NoticeFor(arrivals, vaultOpen) is not { } notice)
        {
            return;
        }

        Show(notice.SelectOrderId, notice.Text);
    }

    /// <summary>The vault opened: whatever the notices said, the curator is looking at it now. Closes every notice.</summary>
    internal static void Seen()
    {
        foreach (NoticePopup popup in Shown.ToList())
        {
            popup.Close();
        }
    }

    /// <summary>Drops everything. Called from <c>Terminate</c>.</summary>
    internal static void Forget()
    {
        Seen();
        Shown.Clear();
    }

    private static void Show(string? selectOrderId, string text)
    {
        IntPtr hostWindow = MantlePlaceApplication.MainWindowHandle();

        // Minimised, or not up yet: nowhere to put a notice that belongs to AutoCAD's window. The vault
        // lists the order when the curator opens it, which is what a notice leads to anyway.
        if (hostWindow == IntPtr.Zero || HostWindowGeometry.IsMinimised(hostWindow))
        {
            return;
        }

        // One notice per bundle on screen: the newer replaces the older.
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

/// <summary>Opens the vault browser: one window per session, and a second request focuses it.</summary>
internal static class VaultBrowser
{
    private static VaultBrowserWindow? _window;

    /// <summary>Whether the vault browser is open, which silences every notice and pauses the background listing (HPS-55).</summary>
    internal static bool IsOpen => _window is not null;

    /// <summary>Opens the vault browser, or brings the open one forward, with <paramref name="selectOrderId"/> selected when it names a row. AutoCAD's UI thread only.</summary>
    internal static void Open(IntPtr hostWindow, string? selectOrderId)
    {
        // Opening the vault is seeing what the notices said, whichever way it was opened.
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
            hostWindow,
            selectOrderId);

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    /// <summary>Closes the window, if open. Called from <c>Terminate</c>.</summary>
    internal static void Close() => _window?.Close();
}
