namespace MantlePlace.AutoCad;

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

public static class Presence
{
    public const string DecidingModule = "AeccLand.dbx";

    public static PresenceVerdict Decide(PresenceObservations seen)
    {
        ArgumentNullException.ThrowIfNull(seen);

        if (seen.CivilNativeLoaded)
        {
            return PresenceVerdict.Civil3dLoaded;
        }

        return seen.CivilFilesOnDisk ? PresenceVerdict.InstalledNotLoaded : PresenceVerdict.Absent;
    }

    public static bool MayLoadCivil3dPart(PresenceVerdict verdict) => verdict == PresenceVerdict.Civil3dLoaded;
}
