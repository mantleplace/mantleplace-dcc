using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

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
                AddinFaults.IsOurs(["MantlePlace.Client.AuthSession"]),
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
            run.True(
                AddinFaults.IsOurs([
                    "System.Threading.CancellationTokenSource",
                    "MantlePlace.Client.AuthSession",
                    "MantlePlace.Revit.Addin.SignInWindow",
                    "System.Windows.Window",
                ]),
                "ObjectDisposedException from Cancel(), raised by our Closed handler");
        });

        run.Case("a null declaring type is skipped, not guessed at", () =>
        {
            run.False(AddinFaults.IsOurs([null, null]), "an unreadable stack claims nothing");
            run.True(
                AddinFaults.IsOurs([null, "MantlePlace.Revit.Addin.VaultBrowserWindow"]),
                "but one unreadable frame does not hide a readable one");
        });

        run.Case("a lookalike namespace is not ours", () =>
        {
            run.False(
                AddinFaults.IsOurs(["MantlePlaceholder.Widgets.Thing"]),
                "a namespace that merely starts with the same letters is somebody else's");
        });

        return run.Report("addin faults");
    }
}
