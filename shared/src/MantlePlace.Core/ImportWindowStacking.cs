namespace MantlePlace.Core;

public enum ForegroundHolder
{
    Nobody,

    ImportWindow,

    RevitMainWindow,

    RevitOtherWindow,

    RevitModal,

    RevitGhost,

    Desktop,

    OtherProcess,
}

public readonly record struct ForegroundFacts(
    bool Exists,
    bool IsImportWindow,
    bool InThisProcess,
    bool IsRevitMainWindow,
    bool RevitMainWindowEnabled,
    bool ForegroundFloats,
    string ClassName,
    bool? HungWindowOfThisProcess);

public enum StackingMove
{
    None,

    Float,

    SinkBehindForeground,

    SinkBelowFloating,
}

public readonly record struct Stacking(bool Floating, StackingMove Move);

public static class ImportWindowStacking
{
    private static readonly string[] DesktopClasses = ["Progman", "WorkerW"];

    private const string GhostClass = "Ghost";

    public static ForegroundHolder Classify(ForegroundFacts facts)
    {
        if (!facts.Exists)
        {
            return ForegroundHolder.Nobody;
        }

        bool ghost = string.Equals(facts.ClassName, GhostClass, StringComparison.Ordinal);
        if (ghost && facts.HungWindowOfThisProcess is null)
        {
            return ForegroundHolder.Nobody;
        }

        bool revits = facts.InThisProcess || (ghost && facts.HungWindowOfThisProcess == true);
        if (revits && !facts.RevitMainWindowEnabled)
        {
            return ForegroundHolder.RevitModal;
        }

        if (ghost)
        {
            return revits ? ForegroundHolder.RevitGhost : ForegroundHolder.OtherProcess;
        }

        if (facts.IsImportWindow)
        {
            return ForegroundHolder.ImportWindow;
        }

        if (facts.InThisProcess)
        {
            return facts.IsRevitMainWindow ? ForegroundHolder.RevitMainWindow : ForegroundHolder.RevitOtherWindow;
        }

        return Array.IndexOf(DesktopClasses, facts.ClassName) >= 0 ? ForegroundHolder.Desktop : ForegroundHolder.OtherProcess;
    }

    public static Stacking First(ForegroundFacts facts)
    {
        if (ShowsActivated(facts))
        {
            return new Stacking(true, StackingMove.Float);
        }

        return Floats(Classify(facts)) switch
        {
            true => new Stacking(true, StackingMove.Float),
            false => new Stacking(false, Sink(facts)),
            null => new Stacking(false, StackingMove.None),
        };
    }

    public static Stacking Next(ForegroundFacts facts, bool floating) => Floats(Classify(facts)) switch
    {
        true => new Stacking(true, floating ? StackingMove.None : StackingMove.Float),

        false => new Stacking(false, floating ? Sink(facts) : StackingMove.None),
        null => new Stacking(floating, StackingMove.None),
    };

    public static Stacking Lowered(bool floating)
        => floating ? new Stacking(false, StackingMove.SinkBelowFloating) : new Stacking(false, StackingMove.None);

    public static bool ShowsActivated(ForegroundFacts facts)
        => Classify(facts) is ForegroundHolder.RevitMainWindow or ForegroundHolder.RevitOtherWindow;

    public static bool BringsForward(ForegroundFacts facts) => Classify(facts) != ForegroundHolder.RevitModal;

    public static bool ComesForwardAtTheEnd(ForegroundFacts facts)
        => Classify(facts) is ForegroundHolder.ImportWindow or ForegroundHolder.RevitMainWindow or ForegroundHolder.RevitOtherWindow;

    private static bool? Floats(ForegroundHolder holder) => holder switch
    {
        ForegroundHolder.ImportWindow or ForegroundHolder.RevitMainWindow or ForegroundHolder.RevitGhost => true,
        ForegroundHolder.RevitOtherWindow or ForegroundHolder.RevitModal or ForegroundHolder.OtherProcess => false,
        _ => null,
    };

    private static StackingMove Sink(ForegroundFacts facts)
        => facts.Exists && !facts.IsImportWindow && !facts.ForegroundFloats
            ? StackingMove.SinkBehindForeground
            : StackingMove.SinkBelowFloating;
}

public static class ImportWindowPlacement
{
    public static ScreenRect CentreOver(ScreenRect revit, double width, double height)
        => new(
            revit.Left + Math.Max(0, (revit.Width - width) / 2),
            revit.Top + Math.Max(0, (revit.Height - height) / 2),
            width,
            height);

    public static ScreenRect Restored(ScreenRect normalInWorkspace, ScreenRect workArea, ScreenRect monitor, bool restoresMaximised)
        => restoresMaximised
            ? workArea
            : normalInWorkspace with
            {
                Left = normalInWorkspace.Left + (workArea.Left - monitor.Left),
                Top = normalInWorkspace.Top + (workArea.Top - monitor.Top),
            };
}
