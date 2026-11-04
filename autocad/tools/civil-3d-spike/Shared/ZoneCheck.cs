namespace MantlePlace.AutoCad.Civil3dSpike;

public enum ZoneVerdict
{
    Proceed,
    RefuseMismatch,
    RefuseNoDrawingZone,
    RefuseNoOrderZone,
}

public sealed record ZoneResult(ZoneVerdict Verdict, string Reason);

/// <summary>
/// The comparison the plugin makes between the drawing's coordinate zone code and the order's, before
/// it places anything. It only compares: a refusal says why and leaves the drawing as it was, and the
/// zone is never set for the user. Codes compare exactly (ordinal), because a code is an identifier the
/// plugin passes through, not a value it interprets.
/// </summary>
public static class ZoneCheck
{
    /// <summary>The code a drawing with no coordinate system reads, and the first of <c>GetAllCodes()</c>.</summary>
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
