namespace MantlePlace.AutoCad.Civil3dSpike;

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
    public static PresenceVerdict Decide(PresenceObservations seen)
    {
        if (seen.CivilNativeLoaded)
        {
            return PresenceVerdict.Civil3dLoaded;
        }

        return seen.CivilFilesOnDisk ? PresenceVerdict.InstalledNotLoaded : PresenceVerdict.Absent;
    }
}
