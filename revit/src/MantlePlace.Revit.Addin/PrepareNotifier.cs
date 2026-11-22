using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

internal static class PrepareNotifier
{
    private static readonly PendingNotices Pending = new();

    private static readonly List<NoticePopup> Shown = [];

    private static PushButton? _vaultButton;
    private static Func<IntPtr>? _revitWindow;

    internal static void Attach(PushButton vaultButton, Func<IntPtr> revitWindow)
    {
        _vaultButton = vaultButton;
        _revitWindow = revitWindow;
    }

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

    internal static void Seen()
    {
        Pending.Clear();
        RepaintBadge();

        foreach (NoticePopup popup in Shown.ToList())
        {
            popup.Close();
        }
    }

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

        if (revitWindow == IntPtr.Zero || HostWindowGeometry.IsMinimised(revitWindow))
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
        if (HostWindowGeometry.Bounds(_revitWindow?.Invoke() ?? IntPtr.Zero) is not { } owner)
        {
            return;
        }

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
        }
    }
}
