using MantlePlace.Core;

namespace MantlePlace.AutoCad;

/// <summary>
/// One AutoCAD import as the shared import window shows it (<c>HPS-60</c>): its steps in the order they
/// run, each one's state, the step in flight, and Cancel between steps. Pure; owned by AutoCAD's thread.
/// </summary>
/// <remarks>
/// <para>
/// The steps are this host's own, so each row carries its own name (<see cref="ImportRunRow.Named"/>):
/// first <see cref="VerifyStep"/>, which finds every placed part's file and matches it to its published
/// digest before anything is drawn (<c>HPS-26</c>), then each placed part in the order the shell draws
/// them, the aerial first so the linework draws over it. The surface is a step only where the Civil 3D
/// part is on to build it.
/// </para>
/// <para>
/// Every step is one transaction, so a cancel is honoured between steps and never inside one: the step
/// in flight finishes, and the steps it never reached read <c>Not Run</c>. A step that fails stops
/// nothing but itself, as a part AutoCAD refuses always has, except the verification: a file that
/// fails its digest refuses the whole import with nothing in the drawing, so nothing after it runs.
/// </para>
/// </remarks>
public sealed class AutoCadImportRun
{
    /// <summary>The first step's row: every placed part's file, found and checked before anything is drawn.</summary>
    public const string VerifyStep = "Verify Files";

    /// <summary>What a part the import never reached says in the closing report.</summary>
    public const string NotReachedReason = "the import was cancelled before it ran.";

    /// <summary>The window's one line for a cancelled run.</summary>
    public const string CancelledOutcome = "Cancelled. What was placed before the cancel stays in the drawing.";

    /// <summary>The order the shell draws the parts in: the aerial under the linework, the records and linework on top.</summary>
    public static IReadOnlyList<AutoCadPart> DrawOrder { get; } =
        [AutoCadPart.Aerial, AutoCadPart.Surface, AutoCadPart.Contours, AutoCadPart.Records, AutoCadPart.Linework];

    private readonly AutoCadPart?[] _parts;
    private readonly ImportStepState[] _states;
    private bool _cancelAsked;

    /// <param name="plan">The plan, not refused.</param>
    /// <param name="civil3d">Whether the Civil 3D part is on, which is what builds the surface.</param>
    public AutoCadImportRun(AutoCadImportPlan plan, bool civil3d)
    {
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
    }

    /// <summary>How many steps the run has, the verification included.</summary>
    public int Count => _parts.Length;

    /// <summary>The part step <paramref name="index"/> places, or <c>null</c> for the verification.</summary>
    public AutoCadPart? PartAt(int index) => _parts[index];

    /// <summary>Where step <paramref name="index"/> stands.</summary>
    public ImportStepState StateAt(int index) => _states[index];

    /// <summary>Whether Cancel has been pressed.</summary>
    public bool CancelRequested => _cancelAsked;

    /// <summary>Whether a cancel stopped a step from running.</summary>
    public bool WasCancelled => _cancelAsked && _states.Contains(ImportStepState.NotRun);

    /// <summary>Whether the run is over: no step waits or runs.</summary>
    public bool IsOver => !_states.Any(state => state is ImportStepState.Waiting or ImportStepState.Importing);

    /// <summary>A row's name in the window, the closing report's word for the part.</summary>
    public static string StepName(AutoCadPart part) => part switch
    {
        AutoCadPart.Contours => "Contours",
        AutoCadPart.Surface => "Civil 3D Surface",
        AutoCadPart.Aerial => "Aerial Image",
        AutoCadPart.Records => "Public-Record Layers",
        AutoCadPart.Linework => "Site Linework",
        _ => part.ToString(),
    };

    /// <summary>The curator pressed Cancel, or closed the window: the run stops at its next boundary.</summary>
    public void RequestCancel() => _cancelAsked = true;

    /// <summary>
    /// The next step to run, marked <c>Importing</c>; or <c>null</c> when the run is over, which a waiting
    /// cancel makes it, every step it never reached then reading <c>Not Run</c>.
    /// </summary>
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
        return next;
    }

    /// <summary>
    /// Step <paramref name="index"/> ended. A failed verification stops the run: nothing after it runs.
    /// </summary>
    public void End(int index, bool succeeded)
    {
        if (_states[index] != ImportStepState.Importing)
        {
            throw new InvalidOperationException($"Step {index} is not running.");
        }

        _states[index] = succeeded ? ImportStepState.Done : ImportStepState.Failed;
        if (!succeeded && _parts[index] is null)
        {
            Stop();
        }
    }

    /// <summary>Every step not yet run reads <c>Not Run</c>: the run stopped short (a cancel, a closed drawing, a fault).</summary>
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

    /// <summary>The run as the window shows it now.</summary>
    public ImportRunView View()
    {
        ImportRunRow[] rows = [.. _parts.Select((part, i) => ImportRunRow.Named(part is { } kind ? StepName(kind) : VerifyStep, _states[i]))];
        return ImportRunView.From(rows, Array.IndexOf(_states, ImportStepState.Importing));
    }

    /// <summary>
    /// What the closing report says of each part the run never reached, in place of the plan's sentence,
    /// which would say it was placed.
    /// </summary>
    /// <param name="why">Why it never ran, ending in a full stop; <see cref="NotReachedReason"/> for a cancel.</param>
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

    /// <summary>How the run ended, as the web hears it.</summary>
    /// <param name="placedAnything">Whether any part went into the drawing.</param>
    public ImportEnding Ending(bool placedAnything)
        => WasCancelled ? ImportEnding.CancelledWhileRunning
            : placedAnything ? ImportEnding.Imported
            : ImportEnding.Failed;
}
