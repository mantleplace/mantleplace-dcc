namespace MantlePlace.AutoCad.Civil3dSpike;

/// <summary>What the probe can see from an assembly that references no Civil 3D code.</summary>
/// <param name="CivilManagedLoaded">An assembly named <c>AeccDbMgd</c> is loaded in some load context.</param>
/// <param name="CivilNativeLoaded">Civil 3D's land module, <c>AeccLand.dbx</c>, is a loaded module of the process.</param>
/// <param name="AecBaseLoaded"><c>AecBase.dbx</c>, the module the Civil 3D launcher loads with <c>/ld</c>, is loaded.</param>
/// <param name="Product">The <c>PRODUCT</c> system variable.</param>
/// <param name="CivilFilesOnDisk"><c>C3D\AeccDbMgd.dll</c> exists beside the host executable: installed, which says nothing about loaded.</param>
public sealed record PresenceObservations(
    bool CivilManagedLoaded,
    bool CivilNativeLoaded,
    bool AecBaseLoaded,
    string Product,
    bool CivilFilesOnDisk);

public enum PresenceVerdict
{
    Civil3dLoaded,
    InstalledNotLoaded,
    Absent,
}

/// <summary>
/// The presence test: whether the Civil 3D part may load. It is Civil 3D's land module,
/// <c>AeccLand.dbx</c>, being a loaded module of the process, which told Civil 3D from its "as AutoCAD"
/// launcher in every run, in the console and in the window (../../../docs/api-record.md). Files on disk are never
/// enough: that launcher runs the same executable over the same folder with nothing of Civil 3D
/// loaded. <c>PRODUCT</c> reads "AutoCAD" in Civil 3D too, and <c>AeccDbMgd</c> is not yet loaded in
/// Civil 3D's console, so neither decides.
/// </summary>
public static class Presence
{
    public static PresenceVerdict Decide(PresenceObservations seen)
    {
        if (seen.CivilNativeLoaded)
        {
            return PresenceVerdict.Civil3dLoaded;
        }

        return seen.CivilFilesOnDisk ? PresenceVerdict.InstalledNotLoaded : PresenceVerdict.Absent;
    }
}
