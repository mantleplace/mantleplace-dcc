using MantlePlace.Core;
using MantlePlace.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

internal static class AutoCadImportRunTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the steps are the verification, then each placed part in the order the shell draws it", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial, AutoCadPart.Linework), civil3d: false);

            run.Equal(Names(import), "Verify Files|Aerial Image|Contours|Site Linework", "the aerial under the linework");
            run.Equal(States(import), "Waiting|Waiting|Waiting|Waiting", "every step listed before any runs");
            run.Equal(import.View().CurrentIndex, -1, "none in flight yet");
        });

        run.Case("the surface is a step only where the Civil 3D part is on to build it", () =>
        {
            AutoCadImportPlan plan = Plan(AutoCadPart.Contours, AutoCadPart.Surface, AutoCadPart.Records);

            run.Equal(Names(new AutoCadImportRun(plan, civil3d: true)), "Verify Files|Civil 3D Surface|Contours|Public-Record Layers", "Civil 3D");
            run.Equal(Names(new AutoCadImportRun(plan, civil3d: false)), "Verify Files|Contours|Public-Record Layers", "plain AutoCAD");
        });

        run.Case("a part the plan skips is no step", () =>
            run.Equal(Names(new AutoCadImportRun(Plan(AutoCadPart.Contours), civil3d: true)), "Verify Files|Contours", "only what is placed"));

        run.Case("each step runs in turn, the one in flight reads Importing, and the run ends", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial), civil3d: false);

            run.Equal(import.Next() ?? -1, 0, "the verification first");
            run.Equal(import.View().CurrentName, AutoCadImportRun.VerifyStep, "the window names it");
            import.End(0, succeeded: true);
            run.Equal(import.Next() ?? -1, 1, "then the aerial");
            run.Equal(States(import), "Done|Importing|Waiting", "the current step moves");
            run.True(import.PartAt(1) == AutoCadPart.Aerial, "the shell is handed the part");
            import.End(1, succeeded: false);
            run.Equal(import.Next() ?? -1, 2, "a part AutoCAD refused stops nothing but itself");
            import.End(2, succeeded: true);
            run.True(import.Next() is null, "then the run is over");
            run.Equal(States(import), "Done|Failed|Done", "each step's own outcome");
            run.True(import.IsOver, "nothing waits");
            run.Equal(import.Ending(placedAnything: true), ImportEnding.Imported, "the web hears it imported");
        });

        run.Case("Cancel is honoured between steps: the step in flight finishes, the rest read Not Run", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial, AutoCadPart.Linework), civil3d: false);
            import.End(import.Next()!.Value, succeeded: true);
            int aerial = import.Next()!.Value;

            import.RequestCancel();
            run.Equal(States(import), "Done|Importing|Waiting|Waiting", "the press stops nothing mid-step");
            import.End(aerial, succeeded: true);
            run.True(import.Next() is null, "the next boundary ends the run");
            run.Equal(States(import), "Done|Done|Not Run|Not Run", "HPS-51's words for a step never reached");
            run.True(import.WasCancelled, "a cancelled run");
            run.Equal(import.Ending(placedAnything: true), ImportEnding.CancelledWhileRunning, "and the web hears it was cancelled");
        });

        run.Case("a cancel says each part it never reached was not imported, never that it was placed", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial), civil3d: false);
            import.End(import.Next()!.Value, succeeded: true);
            import.RequestCancel();
            _ = import.Next();

            IReadOnlyDictionary<AutoCadPart, string> said = import.NotReached(AutoCadImportRun.NotReachedReason);
            run.Equal(string.Join(",", said.Keys.OrderBy(k => k)), "Contours,Aerial", "both parts, and not the verification");
            run.Contains(said[AutoCadPart.Aerial], "not imported, because the import was cancelled before it ran.", "the reason");
        });

        run.Case("a cancel after the last step cancels nothing", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours), civil3d: false);
            import.End(import.Next()!.Value, succeeded: true);
            import.End(import.Next()!.Value, succeeded: true);
            import.RequestCancel();

            run.True(import.Next() is null, "over");
            run.False(import.WasCancelled, "every step ran");
            run.Equal(import.Ending(placedAnything: true), ImportEnding.Imported, "imported");
        });

        run.Case("a file that fails its check stops the run before anything is drawn", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial), civil3d: false);
            import.End(import.Next()!.Value, succeeded: false);

            run.True(import.Next() is null, "nothing after it runs");
            run.Equal(States(import), "Failed|Not Run|Not Run", "the verification failed, the parts never ran");
            run.False(import.WasCancelled, "not a cancel");
            run.Equal(import.Ending(placedAnything: false), ImportEnding.Failed, "the web hears it failed");
        });

        run.Case("a run stopped short by the shell (a closed drawing, a fault) leaves the rest Not Run", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial), civil3d: false);
            import.End(import.Next()!.Value, succeeded: true);
            import.Stop();

            run.True(import.Next() is null, "over");
            run.Equal(States(import), "Done|Not Run|Not Run", "the parts never ran");
        });

        run.Case("one step at a time", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours), civil3d: false);
            _ = import.Next();
            run.Throws<InvalidOperationException>(() => _ = import.Next(), "a second step before the first ends");
        });

        run.Case("each step's time is kept in seconds and listed for the closing report", () =>
        {
            TimeSpan now = TimeSpan.Zero;
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours, AutoCadPart.Aerial, AutoCadPart.Linework), () => now, civil3d: false);

            run.True(import.StepTimes() is null, "nothing ran, nothing to list");

            now = TimeSpan.FromSeconds(10);
            int verify = import.Next()!.Value;
            now = TimeSpan.FromSeconds(10.4);
            import.End(verify, succeeded: true);
            int aerial = import.Next()!.Value;
            now = TimeSpan.FromSeconds(13.65);
            import.End(aerial, succeeded: false);
            _ = import.Next();

            run.True(Math.Abs((import.SecondsAt(0) ?? -1) - 0.4) < 0.0001, "the verification took four tenths");
            run.True(Math.Abs((import.SecondsAt(1) ?? -1) - 3.25) < 0.0001, "the aerial took 3.25");
            run.True(import.SecondsAt(2) is null, "a step in flight has no time yet");
            run.True(import.SecondsAt(3) is null, "a step never reached has none");

            now = TimeSpan.FromSeconds(14);
            import.End(2, succeeded: true);
            import.Stop();

            run.Equal(
                import.StepTimes()!,
                "Step times (seconds)" + Environment.NewLine + "Verify Files: 0.40" + Environment.NewLine + "Aerial Image: 3.25" + Environment.NewLine + "Contours: 0.35" + Environment.NewLine + "Site Linework: not run",
                "one line a step, in the window's order");
        });

        run.Case("the step times name no path, order or drawing", () =>
        {
            AutoCadImportRun import = new(Plan(AutoCadPart.Contours), civil3d: false);
            import.End(import.Next()!.Value, succeeded: true);
            import.End(import.Next()!.Value, succeeded: true);

            string times = import.StepTimes()!;
            run.False(times.Contains("order-1", StringComparison.Ordinal), "no order id");
            run.False(times.Contains(BackSlash, StringComparison.Ordinal), "no path");
        });

        run.Case("a refused plan has no run", () =>
            run.Throws<ArgumentException>(() => _ = new AutoCadImportRun(new AutoCadImportPlan { Refusal = "no." }, civil3d: false), "refused"));

        return run.Report("AutoCAD import run");
    }

    private static AutoCadImportPlan Plan(params AutoCadPart[] placed) => new()
    {
        OrderId = "order-1",
        Parts =
        [
            .. Enum.GetValues<AutoCadPart>().Select(kind => new PlannedPart
            {
                Kind = kind,
                Place = placed.Contains(kind),
                Present = placed.Contains(kind),
                Ready = placed.Contains(kind),
                Sentence = $"{kind} planned.",
            }),
        ],
    };

    private const string BackSlash = "\\";

    private static string Names(AutoCadImportRun import) => string.Join("|", import.View().Rows.Select(row => row.StepName));

    private static string States(AutoCadImportRun import) => string.Join("|", import.View().Rows.Select(row => WindowLabels.StateWord(row.State)));
}
