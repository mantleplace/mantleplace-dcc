using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The presence-gate decision: whether the Civil 3D part may load. The cases are the rows of the
/// spike's table (<c>autocad/docs/api-record.md</c>), each as the observations the host gave.
/// </summary>
internal static class PresenceTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("Civil 3D's land module loaded in the process is the one thing that lets the part load", () =>
        {
            PresenceVerdict verdict = Presence.Decide(new PresenceObservations(
                CivilManagedLoaded: true, CivilNativeLoaded: true, AecBaseLoaded: true, Product: "AutoCAD", CivilFilesOnDisk: true));

            run.Equal(verdict, PresenceVerdict.Civil3dLoaded, "Civil 3D, window");
            run.True(Presence.MayLoadCivil3dPart(verdict), "so the part may load");
        });

        run.Case("Civil 3D's console, where AeccDbMgd is not yet loaded, still loads the part", () =>
        {
            PresenceVerdict verdict = Presence.Decide(new PresenceObservations(
                CivilManagedLoaded: false, CivilNativeLoaded: true, AecBaseLoaded: true, Product: "AutoCAD", CivilFilesOnDisk: true));

            run.Equal(verdict, PresenceVerdict.Civil3dLoaded, "a managed-assembly test would have turned the part off here");
            run.True(Presence.MayLoadCivil3dPart(verdict), "the part may load");
        });

        run.Case("Civil 3D's \"as AutoCAD\" launcher has its files on disk and nothing loaded, so the part stays off", () =>
        {
            PresenceVerdict verdict = Presence.Decide(new PresenceObservations(
                CivilManagedLoaded: false, CivilNativeLoaded: false, AecBaseLoaded: false, Product: "AutoCAD", CivilFilesOnDisk: true));

            run.Equal(verdict, PresenceVerdict.InstalledNotLoaded, "installed is not loaded");
            run.False(Presence.MayLoadCivil3dPart(verdict), "so the part must not load");
        });

        run.Case("plain AutoCAD has no sign of Civil 3D, and the part stays off", () =>
        {
            PresenceVerdict verdict = Presence.Decide(new PresenceObservations(
                CivilManagedLoaded: false, CivilNativeLoaded: false, AecBaseLoaded: false, Product: "AutoCAD", CivilFilesOnDisk: false));

            run.Equal(verdict, PresenceVerdict.Absent, "nothing of Civil 3D");
            run.False(Presence.MayLoadCivil3dPart(verdict), "the part must not load");
        });

        run.Case("none of the signals that failed to decide in the spike can turn the part on by itself", () =>
        {
            // AecBase.dbx is the AutoCAD verticals' shared base, PRODUCT reads AutoCAD in Civil 3D too,
            // and AeccDbMgd is loaded lazily: each alone is what a wrong test would have trusted.
            PresenceVerdict baseOnly = Presence.Decide(new PresenceObservations(false, false, true, "AutoCAD", true));
            PresenceVerdict managedOnly = Presence.Decide(new PresenceObservations(true, false, false, "AutoCAD", true));
            PresenceVerdict product = Presence.Decide(new PresenceObservations(false, false, false, "Civil 3D", true));

            run.False(Presence.MayLoadCivil3dPart(baseOnly), "AecBase.dbx alone is not Civil 3D");
            run.False(Presence.MayLoadCivil3dPart(managedOnly), "a loaded managed assembly alone is not the test");
            run.False(Presence.MayLoadCivil3dPart(product), "the product name is not the test");
        });

        run.Case("the deciding module is the land module", () =>
            run.Equal(Presence.DecidingModule, "AeccLand.dbx", "the module the shell asks the process about"));

        return run.Report("Presence");
    }
}
