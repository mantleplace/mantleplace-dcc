// Headless tests of the spike's pure decisions: the zone comparison the plugin will make before it
// places anything, and the presence verdict that decides whether the Civil 3D part may load.
// Run from this folder: dotnet run -f net8.0 (then net10.0).
using MantlePlace.AutoCad.Civil3dSpike;

int failed = 0;
int passed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine($"  pass  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}: {ex.Message}");
    }
}

static void Equal<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{what}: expected <{expected}>, got <{actual}>");
    }
}

Console.WriteLine("zone comparison");

Test("the order's zone in the drawing proceeds", () =>
    Equal(ZoneVerdict.Proceed, ZoneCheck.Compare("CA83-IIIF", "CA83-IIIF").Verdict, "verdict"));

Test("a different zone in the drawing refuses, naming both", () =>
{
    var result = ZoneCheck.Compare("UTM83-10", "CA83-IIIF");
    Equal(ZoneVerdict.RefuseMismatch, result.Verdict, "verdict");
    Equal(true, result.Reason.Contains("UTM83-10") && result.Reason.Contains("CA83-IIIF"), "reason names both codes");
});

Test("a drawing with no zone refuses: it is never set for the user", () =>
{
    Equal(ZoneVerdict.RefuseNoDrawingZone, ZoneCheck.Compare("", "CA83-IIIF").Verdict, "empty");
    Equal(ZoneVerdict.RefuseNoDrawingZone, ZoneCheck.Compare(null, "CA83-IIIF").Verdict, "null");
});

Test("a drawing reading \".\", Civil 3D's code for no coordinate system, has no zone", () =>
    Equal(ZoneVerdict.RefuseNoDrawingZone, ZoneCheck.Compare(".", "CA83IIIF").Verdict, "verdict"));

Test("an order with no zone refuses rather than matching an empty drawing", () =>
    Equal(ZoneVerdict.RefuseNoOrderZone, ZoneCheck.Compare("", "").Verdict, "verdict"));

Test("codes compare exactly: a case difference is a mismatch, never a guess", () =>
    Equal(ZoneVerdict.RefuseMismatch, ZoneCheck.Compare("ca83-iiif", "CA83-IIIF").Verdict, "verdict"));

Console.WriteLine("presence");

// The observations below are the ones the probe recorded on 2026-11-03 (../../../docs/api-record.md).
Test("Civil 3D in the console: the land module is loaded while AeccDbMgd is not, and PRODUCT says AutoCAD", () =>
    Equal(PresenceVerdict.Civil3dLoaded, Presence.Decide(new PresenceObservations(
        CivilManagedLoaded: false, CivilNativeLoaded: true, AecBaseLoaded: true, Product: "AutoCAD", CivilFilesOnDisk: true)), "verdict"));

Test("Civil 3D in the window: everything is loaded", () =>
    Equal(PresenceVerdict.Civil3dLoaded, Presence.Decide(new PresenceObservations(
        CivilManagedLoaded: true, CivilNativeLoaded: true, AecBaseLoaded: true, Product: "AutoCAD", CivilFilesOnDisk: true)), "verdict"));

Test("the \"as AutoCAD\" launcher: Civil 3D's files are on disk and nothing of it is loaded", () =>
    Equal(PresenceVerdict.InstalledNotLoaded, Presence.Decide(new PresenceObservations(
        CivilManagedLoaded: false, CivilNativeLoaded: false, AecBaseLoaded: false, Product: "AutoCAD", CivilFilesOnDisk: true)), "verdict"));

Test("plain AutoCAD with no Civil 3D installed", () =>
    Equal(PresenceVerdict.Absent, Presence.Decide(new PresenceObservations(
        CivilManagedLoaded: false, CivilNativeLoaded: false, AecBaseLoaded: false, Product: "AutoCAD", CivilFilesOnDisk: false)), "verdict"));

Test("AecBase alone is not Civil 3D: other AutoCAD verticals load it too", () =>
    Equal(PresenceVerdict.InstalledNotLoaded, Presence.Decide(new PresenceObservations(
        CivilManagedLoaded: false, CivilNativeLoaded: false, AecBaseLoaded: true, Product: "AutoCAD", CivilFilesOnDisk: true)), "verdict"));

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
