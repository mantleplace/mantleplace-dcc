using System.Diagnostics;
using System.Globalization;
using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public sealed class AutoCadImportRun
{
    public const string VerifyStep = "Verify Files";

    public const string NotReachedReason = "the import was cancelled before it ran.";

    public const string CancelledOutcome = "Cancelled. What was placed before the cancel stays in the drawing.";

    public static IReadOnlyList<AutoCadPart> DrawOrder { get; } =
        [AutoCadPart.Aerial, AutoCadPart.Surface, AutoCadPart.Contours, AutoCadPart.Records, AutoCadPart.Linework];

    private readonly AutoCadPart?[] _parts;
    private readonly ImportStepState[] _states;
    private readonly TimeSpan?[] _started;
    private readonly TimeSpan?[] _ended;
    private readonly Func<TimeSpan> _clock;
    private bool _cancelAsked;

    public AutoCadImportRun(AutoCadImportPlan plan, bool civil3d)
        : this(plan, WallClock(), civil3d)
    {
    }

    public AutoCadImportRun(AutoCadImportPlan plan, Func<TimeSpan> clock, bool civil3d)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;

        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsRefused)
        {
            throw new ArgumentException("A refused plan has no steps to run.", nameof(plan));
        }

        List<AutoCadPart?> parts = [null];
        parts.AddRange(DrawOrder
            .Where(kind => plan.Part(kind).Place && (kind != AutoCadPart.Surface || civil3d))
            .Select(kind => (AutoCadPart?)kind));
        _parts = [.. parts];
        _states = [.. _parts.Select(_ => ImportStepState.Waiting)];
        _started = new TimeSpan?[_parts.Length];
        _ended = new TimeSpan?[_parts.Length];
    }

    private static Func<TimeSpan> WallClock()
    {
        Stopwatch watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

    public int Count => _parts.Length;

    public AutoCadPart? PartAt(int index) => _parts[index];

    public ImportStepState StateAt(int index) => _states[index];

    public bool CancelRequested => _cancelAsked;

    public bool WasCancelled => _cancelAsked && _states.Contains(ImportStepState.NotRun);

    public bool IsOver => !_states.Any(state => state is ImportStepState.Waiting or ImportStepState.Importing);

    public static string StepName(AutoCadPart part) => part switch
    {
        AutoCadPart.Contours => "Contours",
        AutoCadPart.Surface => "Civil 3D Surface",
        AutoCadPart.Aerial => "Aerial Image",
        AutoCadPart.Records => "Public-Record Layers",
        AutoCadPart.Linework => "Site Linework",
        _ => part.ToString(),
    };

    public double? SecondsAt(int index)
        => _started[index] is { } started && _ended[index] is { } ended ? (ended - started).TotalSeconds : null;

    public string? StepTimes()
    {
        if (_started.All(started => started is null))
        {
            return null;
        }

        List<string> lines = ["Step times (seconds)"];
        for (int i = 0; i < _parts.Length; i++)
        {
            string name = _parts[i] is { } kind ? StepName(kind) : VerifyStep;
            string said = SecondsAt(i) is { } seconds
                ? seconds.ToString("0.00", CultureInfo.InvariantCulture)
                : _states[i] == ImportStepState.Importing ? "did not finish" : "not run";
            lines.Add($"{name}: {said}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void RequestCancel() => _cancelAsked = true;

    public int? Next()
    {
        if (_states.Contains(ImportStepState.Importing))
        {
            throw new InvalidOperationException("A step is still running; it ends before the next begins.");
        }

        if (_cancelAsked)
        {
            Stop();
            return null;
        }

        int next = Array.IndexOf(_states, ImportStepState.Waiting);
        if (next < 0)
        {
            return null;
        }

        _states[next] = ImportStepState.Importing;
        _started[next] = _clock();
        return next;
    }

    public void End(int index, bool succeeded)
    {
        if (_states[index] != ImportStepState.Importing)
        {
            throw new InvalidOperationException($"Step {index} is not running.");
        }

        _states[index] = succeeded ? ImportStepState.Done : ImportStepState.Failed;
        _ended[index] = _clock();
        if (!succeeded && _parts[index] is null)
        {
            Stop();
        }
    }

    public void Stop()
    {
        for (int i = 0; i < _states.Length; i++)
        {
            if (_states[i] == ImportStepState.Waiting)
            {
                _states[i] = ImportStepState.NotRun;
            }
        }
    }

    public ImportRunView View()
    {
        ImportRunRow[] rows = [.. _parts.Select((part, i) => ImportRunRow.Named(part is { } kind ? StepName(kind) : VerifyStep, _states[i]))];
        return ImportRunView.From(rows, Array.IndexOf(_states, ImportStepState.Importing));
    }

    public IReadOnlyDictionary<AutoCadPart, string> NotReached(string why)
    {
        Dictionary<AutoCadPart, string> said = [];
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_parts[i] is { } part && _states[i] == ImportStepState.NotRun)
            {
                said[part] = AutoCadImportPlanner.FailedSentence(part, why);
            }
        }

        return said;
    }

    public ImportEnding Ending(bool placedAnything)
        => WasCancelled ? ImportEnding.CancelledWhileRunning
            : placedAnything ? ImportEnding.Imported
            : ImportEnding.Failed;
}
