namespace MantlePlace.AutoCad;

/// <summary>What the shell can see from an assembly that references no Civil 3D code.</summary>
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

/// <summary>What the presence test concluded.</summary>
public enum PresenceVerdict
{
    /// <summary>Civil 3D is loaded in this process: the Civil 3D part may load.</summary>
    Civil3dLoaded,

    /// <summary>Civil 3D's files are beside the host, but it is not loaded (AutoCAD running under Civil 3D's "as AutoCAD" launcher).</summary>
    InstalledNotLoaded,

    /// <summary>No sign of Civil 3D: plain AutoCAD.</summary>
    Absent,
}

/// <summary>
/// The presence test: whether the Civil 3D part may load. It is Civil 3D's land module,
/// <c>AeccLand.dbx</c>, being a loaded module of the process, which told Civil 3D from its "as AutoCAD"
/// launcher in every run of the spike, in the console and in the window
/// (<c>autocad/docs/api-record.md</c>). Files on disk are never enough: that launcher runs the same
/// executable over the same folder with nothing of Civil 3D loaded. <c>PRODUCT</c> reads "AutoCAD" in
/// Civil 3D too, and <c>AeccDbMgd</c> is not yet loaded in Civil 3D's console, so neither decides.
/// </summary>
public static class Presence
{
    /// <summary>The module whose presence in the process is the test.</summary>
    public const string DecidingModule = "AeccLand.dbx";

    /// <summary>The verdict for what the shell saw.</summary>
    public static PresenceVerdict Decide(PresenceObservations seen)
    {
        ArgumentNullException.ThrowIfNull(seen);

        if (seen.CivilNativeLoaded)
        {
            return PresenceVerdict.Civil3dLoaded;
        }

        return seen.CivilFilesOnDisk ? PresenceVerdict.InstalledNotLoaded : PresenceVerdict.Absent;
    }

    /// <summary>
    /// Whether the Civil 3D part may be loaded for a verdict. Only <see cref="PresenceVerdict.Civil3dLoaded"/>
    /// allows it: loading the part anywhere else throws <see cref="FileNotFoundException"/> for
    /// <c>AeccDbMgd</c>, which the shell would catch, so a wrong verdict degrades rather than crashes, but
    /// the part is still never loaded on a guess.
    /// </summary>
    public static bool MayLoadCivil3dPart(PresenceVerdict verdict) => verdict == PresenceVerdict.Civil3dLoaded;
}
