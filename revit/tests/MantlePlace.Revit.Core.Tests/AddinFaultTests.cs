using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The one decision behind the shim's dispatcher handler: is this fault ours to swallow?
/// </summary>
/// <remarks>
/// The handler itself cannot be tested without Revit — it lives in <c>MantlePlace.Revit.Addin</c>,
/// which CI does not build. Its judgement can be, and that is the half worth getting right: a
/// predicate that is too generous turns the net into a blanket that hides Revit's own faults, and
/// one that is too strict declines the crash it was written for.
/// </remarks>
internal static class AddinFaultTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a fault with one of our frames is ours", () =>
        {
            run.True(
                AddinFaults.IsOurs(["MantlePlace.Revit.Addin.SignInWindow"]),
                "the shim type that raised the crash this exists for");
            run.True(
                AddinFaults.IsOurs(["MantlePlace.Revit.Client.AuthSession"]),
                "and any other layer of ours");
        });

        run.Case("a fault with no frame of ours is the host's, and stays the host's", () =>
        {
            run.False(
                AddinFaults.IsOurs(["Autodesk.Revit.DB.Document", "System.Windows.Window"]),
                "Revit's own fault passes through untouched -- swallowing it would leave Revit "
                    + "running on state it had already given up on");
            run.False(AddinFaults.IsOurs([]), "and an empty stack claims nothing");
        });

        run.Case("a framework throw from one of our handlers is still ours", () =>
        {
            // This is the actual shape of the crash. The exception type is the framework's and the
            // innermost frame is the framework's; the fault is ours, two frames up. Judging on the
            // throw site alone would decline exactly the case the handler was written for.
            run.True(
                AddinFaults.IsOurs([
                    "System.Threading.CancellationTokenSource",
                    "MantlePlace.Revit.Client.AuthSession",
                    "MantlePlace.Revit.Addin.SignInWindow",
                    "System.Windows.Window",
                ]),
                "ObjectDisposedException from Cancel(), raised by our Closed handler");
        });

        run.Case("a null declaring type is skipped, not guessed at", () =>
        {
            // GetMethod() returns null for frames the runtime elided or never had metadata for.
            // Treating those as ours would let us claim a stack we cannot actually read.
            run.False(AddinFaults.IsOurs([null, null]), "an unreadable stack claims nothing");
            run.True(
                AddinFaults.IsOurs([null, "MantlePlace.Revit.Addin.VaultBrowserWindow"]),
                "but one unreadable frame does not hide a readable one");
        });

        run.Case("a lookalike namespace is not ours", () =>
        {
            // The trailing dot in the prefix is what does this. Without it, an unrelated
            // MantlePlaceSomething type would hand us a fault we did not cause.
            run.False(
                AddinFaults.IsOurs(["MantlePlaceholder.Widgets.Thing"]),
                "a namespace that merely starts with the same letters is somebody else's");
        });

        return run.Report("addin faults");
    }
}
