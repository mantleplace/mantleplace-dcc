using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Tells the curator when a Prepare they stopped watching ends, or when an order nobody asked Revit to
/// prepare turns up in the vault: a notice in the corner of Revit's window for the moment, and a
/// badge on the Vault button for a curator who missed it.
/// </summary>
/// <remarks>
/// <para>
/// The impure half. Whether there is a notice at all, and what it says, is
/// <see cref="PrepareNotices"/> and <see cref="VaultNews"/>; what the badge says is
/// <see cref="VaultBadge"/>; where a notice sits is <see cref="NoticeStack"/> — all in the pure core,
/// where they are asserted without Revit (<c>HPS-02</c>, <c>HPS-42</c>). What is here is windows, a
/// ribbon button, and Win32 for where Revit's window is.
/// </para>
/// <para>
/// ⛔ <b>Revit's UI thread only</b>, every member. <see cref="PrepareWatcher.Ended"/> and
/// <see cref="VaultNewsChecker.Arrived"/> are raised on a thread-pool thread and
/// <c>MantlePlaceApplication</c> hops before calling in, because touching a ribbon button from
/// anywhere else terminates Revit.
/// </para>
/// </remarks>
internal static class PrepareNotifier
{
    private const double BaselineDpi = 96.0;

    private static readonly PendingNotices Pending = new();

    /// <summary>The notices on screen, newest first — index 0 is the bottom of the stack.</summary>
    private static readonly List<NoticePopup> Shown = [];

    private static PushButton? _vaultButton;
    private static Func<IntPtr>? _revitWindow;

    /// <summary>Gives the notifier the button it badges and a way to find Revit's window.</summary>
    internal static void Attach(PushButton vaultButton, Func<IntPtr> revitWindow)
    {
        _vaultButton = vaultButton;
        _revitWindow = revitWindow;
    }

    /// <summary>
    /// A Prepare ended. Tells the curator, if there is anything to tell, and says whether it did.
    /// </summary>
    internal static bool OnEnded(PrepareRun run, bool vaultOpen, bool signedIn)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Ending is not { } ending
            || PrepareNotices.For(run.OrderId, run.Label, ending, run.Detail, vaultOpen, signedIn) is not { } notice)
        {
            return false;
        }

        Pending.Add(notice);
        RepaintBadge();
        Show(notice.OrderId, notice.Text);
        return true;
    }

    /// <summary>
    /// A background listing found orders nobody asked Revit to prepare (<see cref="VaultNewsChecker"/>).
    /// Tells the curator, if there is anything to tell.
    /// </summary>
    internal static void OnArrived(IReadOnlyList<VaultBundle> arrivals, bool vaultOpen)
    {
        if (VaultNews.NoticeFor(arrivals, vaultOpen) is not { } notice)
        {
            return;
        }

        Pending.Add(notice);
        RepaintBadge();
        Show(notice.SelectOrderId, notice.Text);
    }

    /// <summary>
    /// The vault opened: whatever the notices said, the curator is now looking at it. Clears the
    /// badge and closes every notice.
    /// </summary>
    internal static void Seen()
    {
        Pending.Clear();
        RepaintBadge();

        foreach (NoticePopup popup in Shown.ToList())
        {
            popup.Close();
        }
    }

    /// <summary>Drops everything. Called from <c>OnShutdown</c>.</summary>
    internal static void Forget()
    {
        foreach (NoticePopup popup in Shown.ToList())
        {
            popup.Close();
        }

        Shown.Clear();
        Pending.Clear();
        _vaultButton = null;
        _revitWindow = null;
    }

    private static void Show(string? selectOrderId, string text)
    {
        IntPtr revitWindow = _revitWindow?.Invoke() ?? IntPtr.Zero;

        // Minimised, or not up yet: nowhere to put a notice that belongs to Revit's window. The
        // badge is still there when the curator comes back, which is what it is for.
        if (revitWindow == IntPtr.Zero || IsIconic(revitWindow))
        {
            return;
        }

        // One notice per bundle on screen, as on the badge: the newer ending replaces the older.
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

        NoticePopup shown = new(selectOrderId, text, revitWindow);
        shown.Chosen += (_, _) => VaultBrowserCommand.Open(revitWindow, selectOrderId);
        shown.Closed += (_, _) =>
        {
            Shown.Remove(shown);
            Restack();
        };

        Shown.Insert(0, shown);
        Restack();
        shown.Show();
    }

    private static void Restack()
    {
        IntPtr revitWindow = _revitWindow?.Invoke() ?? IntPtr.Zero;
        if (revitWindow == IntPtr.Zero || !GetWindowRect(revitWindow, out NativeRect rect))
        {
            return;
        }

        // GetWindowRect answers in device pixels, WPF places in device-independent ones. Revit's
        // window's own DPI is the divisor, so the stack lands inside it on the monitor it is on.
        double scale = GetDpiForWindow(revitWindow) is var dpi and > 0 ? dpi / BaselineDpi : 1.0;
        NoticeRect owner = new(
            rect.Left / scale,
            rect.Top / scale,
            (rect.Right - rect.Left) / scale,
            (rect.Bottom - rect.Top) / scale);

        for (int index = 0; index < Shown.Count; index++)
        {
            Shown[index].PlaceAt(NoticeStack.Place(owner, index));
        }
    }

    private static void RepaintBadge()
    {
        if (_vaultButton is not { } button)
        {
            return;
        }

        try
        {
            RibbonImagery.SetBadge(button, VaultBadge.CountText(Pending.Count));
            button.ToolTip = VaultBadge.ToolTipFor(Pending.Count);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Swallowed for ApplyAccountState's reason: a ribbon Revit is tearing down refuses the
            // assignment, and a fault dialog over a badge would be worse than a stale one.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
