namespace MantlePlace.Revit.Core;

/// <summary>A rectangle in device-independent pixels.</summary>
public readonly record struct NoticeRect(double Left, double Top, double Width, double Height);

/// <summary>
/// Where each Prepare notice sits: stacked upward from the bottom-right corner of Revit's window.
/// Pure.
/// </summary>
/// <remarks>
/// Inside Revit's window rather than on the desktop's corner, because the notice belongs to this
/// Revit: on two monitors the desktop's corner can be a screen the curator is not looking at. A
/// fixed size, so that stacking is arithmetic rather than a layout pass per notice; a reason too
/// long for it is trimmed on the face and whole in its tooltip.
/// </remarks>
public static class NoticeStack
{
    /// <summary>A notice's width.</summary>
    public const double Width = 340;

    /// <summary>A notice's height.</summary>
    public const double Height = 88;

    /// <summary>Between two stacked notices.</summary>
    public const double Gap = 8;

    /// <summary>Between the bottom notice and the owner's edges, clear of Revit's status bar.</summary>
    public const double Inset = 36;

    /// <summary>
    /// The most notices shown at once. Older ones close early; every bundle is still counted on the
    /// Vault button's badge.
    /// </summary>
    public const int MaxShown = 4;

    /// <summary>The place of the notice at <paramref name="index"/>, 0 being the bottom one.</summary>
    public static NoticeRect Place(NoticeRect owner, int index)
    {
        double left = Math.Max(owner.Left, owner.Left + owner.Width - Inset - Width);
        double top = owner.Top + owner.Height - Inset - Height - (index * (Height + Gap));
        return new NoticeRect(left, top, Width, Height);
    }
}
