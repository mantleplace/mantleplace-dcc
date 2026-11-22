namespace MantlePlace.Core;

public static class NoticeStack
{
    public const double Width = 340;

    public const double Height = 88;

    public const double Gap = 8;

    public const double Inset = 36;

    public const int MaxShown = 4;

    public static ScreenRect Place(ScreenRect owner, int index)
    {
        double left = Math.Max(owner.Left, owner.Left + owner.Width - Inset - Width);
        double top = owner.Top + owner.Height - Inset - Height - (index * (Height + Gap));
        return new ScreenRect(left, top, Width, Height);
    }
}
