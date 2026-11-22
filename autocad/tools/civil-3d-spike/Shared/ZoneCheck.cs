namespace MantlePlace.AutoCad.Civil3dSpike;

public enum ZoneVerdict
{
    Proceed,
    RefuseMismatch,
    RefuseNoDrawingZone,
    RefuseNoOrderZone,
}

public sealed record ZoneResult(ZoneVerdict Verdict, string Reason);

public static class ZoneCheck
{
    public const string NoZone = ".";

    public static ZoneResult Compare(string? drawingCode, string? orderCode)
    {
        if (string.IsNullOrWhiteSpace(orderCode))
        {
            return new ZoneResult(ZoneVerdict.RefuseNoOrderZone, "the order names no coordinate zone");
        }

        if (string.IsNullOrWhiteSpace(drawingCode) || drawingCode == NoZone)
        {
            return new ZoneResult(
                ZoneVerdict.RefuseNoDrawingZone,
                $"the drawing has no coordinate zone; set it to {orderCode} in Drawing Settings, then import again");
        }

        if (!string.Equals(drawingCode, orderCode, StringComparison.Ordinal))
        {
            return new ZoneResult(
                ZoneVerdict.RefuseMismatch,
                $"the drawing's zone is {drawingCode} and the order's is {orderCode}");
        }

        return new ZoneResult(ZoneVerdict.Proceed, $"the drawing's zone is the order's, {orderCode}");
    }
}
