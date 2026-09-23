using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The download at the end of a Prepare.</summary>
/// <param name="Bundle">The re-listed row, carrying the integrity facts the download was checked against.</param>
/// <param name="Ready">Whether the cache now holds a valid zip — what Import requires.</param>
/// <param name="Message">The cache's verdict, or why there is none.</param>
public readonly record struct PreparedDownload(VaultBundle Bundle, bool Ready, string Message);

/// <summary>The three network halves of a Prepare, apart from their order and their bookkeeping.</summary>
/// <remarks>
/// The seam exists so that <see cref="PrepareWatcher"/> — which Prepare runs, which ends how, which
/// one a second click joins — is asserted without a network (<c>HPS-42</c>). The real steps are
/// <see cref="VaultPrepareSteps"/>, a thin pass-through to <see cref="VaultClient"/>.
/// </remarks>
public interface IPrepareSteps
{
    /// <summary>Starts or joins the materialize (<c>HPS-23</c>, <c>HPS-24</c>).</summary>
    Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>Polls to a terminal state, within <c>HPS-25</c>'s bounds.</summary>
    Task<PollOutcome> PollAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        IProgress<MaterializeStatus> progress,
        CancellationToken cancellationToken);

    /// <summary>Re-lists, then downloads into the cache (<c>HPS-18</c>, <c>HPS-26</c>).</summary>
    Task<PreparedDownload> DownloadAsync(VaultBundle bundle, IProgress<string> say, CancellationToken cancellationToken);
}

/// <summary>The steps against the real vault and the real cache.</summary>
public sealed class VaultPrepareSteps : IPrepareSteps
{
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;

    public VaultPrepareSteps(VaultClient vault, BundleCache cache)
    {
        _vault = vault;
        _cache = cache;
    }

    public Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken)
        => _vault.StartMaterializeAsync(orderId, MaterializeJobs.HostScope, cancellationToken);

    public Task<PollOutcome> PollAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        IProgress<MaterializeStatus> progress,
        CancellationToken cancellationToken)
        => _vault.PollToCompletionAsync(orderId, requested, progress, cancellationToken);

    /// <remarks>
    /// The re-list is not optional (<c>HPS-18</c>): it is where the integrity facts for the bundle as
    /// it stands come from, and checking a download against a pre-materialize size and digest is
    /// checking it against numbers that describe a different file.
    /// </remarks>
    public async Task<PreparedDownload> DownloadAsync(
        VaultBundle bundle,
        IProgress<string> say,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(say);

        say.Report("Fetching the integrity details…");
        (VaultListing? relisted, string? listError) = await _vault.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listError is not null)
        {
            return new PreparedDownload(bundle, Ready: false, listError);
        }

        VaultBundle refreshed = relisted!.Bundles
            .FirstOrDefault(row => string.Equals(row.OrderId, bundle.OrderId, StringComparison.Ordinal))
            ?? bundle;

        say.Report("Downloading…");
        string? downloadError = await _vault.DownloadAsync(refreshed, _cache, cancellationToken).ConfigureAwait(false);

        CacheEntry entry = _cache.InspectQuick(
            refreshed.OrderId, refreshed.SizeBytes, refreshed.Sha256, refreshed.ManifestVersion);

        return new PreparedDownload(
            refreshed,
            Ready: downloadError is null && entry.State == CacheState.CachedValid,
            downloadError ?? entry.Describe());
    }
}

/// <summary>
/// One Prepare being watched: materialize → poll → <b>re-list</b> → download, for one order.
/// </summary>
public sealed class PrepareRun
{
    private readonly CancellationTokenSource _cancel = new();
    private volatile string _lastMessage;

    internal PrepareRun(VaultBundle bundle)
    {
        Bundle = bundle;
        OrderId = bundle.OrderId;
        Label = PrepareNotices.LabelOf(bundle.AoiLabel, bundle.OrderId);
        _lastMessage = PrepareMessages.Starting(Label);
    }

    /// <summary>The order being prepared.</summary>
    public string OrderId { get; }

    /// <summary>The bundle's name as the curator reads it.</summary>
    public string Label { get; }

    /// <summary>
    /// The bundle's row: the one Prepare was pressed on until the download, then the re-listed one,
    /// whose size and digest are the ones the cache holds.
    /// </summary>
    public VaultBundle Bundle { get; private set; }

    /// <summary>The newest line this Prepare has said.</summary>
    public string LastMessage => _lastMessage;

    /// <summary>How it ended; <c>null</c> while it runs.</summary>
    public PrepareEnding? Ending { get; private set; }

    /// <summary>The reason, when it ended <see cref="PrepareEnding.Failed"/> or <see cref="PrepareEnding.StillPreparing"/>.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Completes when the Prepare has ended and stopped being watched. Never faults.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <summary>Stops watching, and leaves nothing half-downloaded (<c>HPS-26</c>).</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already ended: there is nothing left to cancel.
        }
    }

    internal CancellationToken Token => _cancel.Token;

    internal void Say(string message) => _lastMessage = message;

    internal void Downloaded(VaultBundle bundle) => Bundle = bundle;

    internal void End(PrepareEnding ending, string detail)
    {
        Ending = ending;
        Detail = detail;
        _cancel.Dispose();
    }
}

/// <summary>
/// Every Prepare this Revit session has running, and the one place that knows whether an order is
/// already being watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Prepare outlives the vault window.</b> A materialize can take ten minutes, and closing the
/// window is "I am done looking", not "throw away the thing I paid for". The window used to own the
/// Prepare anyway: closed, it kept running with no owner, told nobody when it ended, and a reopened
/// window knew nothing of it — so a second Prepare on the same order polled it twice. Owned here, a
/// reopened window shows what is still running, and a second Prepare joins the first
/// (<see cref="Prepare"/>).
/// </para>
/// <para>
/// ⛔ <b>Events are raised on whichever thread finished the work</b>, a thread-pool thread for
/// everything after the first line. Nothing here touches a window or the ribbon; a subscriber that
/// does hops to Revit's UI thread itself, as <c>AuthSession.StateChanged</c>'s subscribers do.
/// </para>
/// </remarks>
public sealed class PrepareWatcher
{
    private readonly IPrepareSteps _steps;
    private readonly object _gate = new();
    private readonly List<PrepareRun> _runs = [];

    public PrepareWatcher(IPrepareSteps steps)
    {
        _steps = steps;
    }

    /// <summary>A Prepare said something new; read it from <see cref="PrepareRun.LastMessage"/>.</summary>
    public event EventHandler<PrepareRun>? Said;

    /// <summary>
    /// A Prepare ended. It is no longer watched when this is raised, and its
    /// <see cref="PrepareRun.Ending"/> says how.
    /// </summary>
    public event EventHandler<PrepareRun>? Ended;

    /// <summary>Every Prepare still being watched, oldest first.</summary>
    public IReadOnlyList<PrepareRun> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>The Prepare watching <paramref name="orderId"/>, or <c>null</c>.</summary>
    public PrepareRun? Watching(string orderId)
    {
        lock (_gate)
        {
            return Find(orderId);
        }
    }

    /// <summary>The run for <paramref name="orderId"/>. Callers hold <see cref="_gate"/>.</summary>
    private PrepareRun? Find(string orderId)
        => _runs.Find(held => string.Equals(held.OrderId, orderId, StringComparison.Ordinal));

    /// <summary>
    /// Starts watching a Prepare of <paramref name="bundle"/>, or joins the one already watching it.
    /// </summary>
    /// <param name="bundle">The row Prepare was pressed on.</param>
    /// <param name="joined">Whether an existing Prepare was joined rather than a new one started.</param>
    /// <remarks>
    /// Joining here is this host's half of single-flight. The platform's half is <c>HPS-24</c>: a
    /// second start would be answered "joined", so no second job would be queued — but it would still
    /// be a second poll loop, spending the rate budget twice on one order.
    /// </remarks>
    public PrepareRun Prepare(VaultBundle bundle, out bool joined)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        PrepareRun run;
        lock (_gate)
        {
            if (Find(bundle.OrderId) is { } existing)
            {
                joined = true;
                return existing;
            }

            run = new PrepareRun(bundle);
            _runs.Add(run);
        }

        joined = false;
        Said?.Invoke(this, run);
        run.Completion = Task.Run(() => WatchAsync(run));
        return run;
    }

    /// <summary>
    /// Cancels every Prepare, for shutdown. It does not wait: a step already in flight finishes on
    /// its own thread, ends <see cref="PrepareEnding.Cancelled"/> or <see cref="PrepareEnding.Failed"/>,
    /// and is never announced, because the shim has stopped listening before it calls this.
    /// </summary>
    public void CancelAll()
    {
        foreach (PrepareRun run in Runs)
        {
            run.Cancel();
        }
    }

    private async Task WatchAsync(PrepareRun run)
    {
        PrepareEnding ending;
        string detail = string.Empty;

        try
        {
            (ending, detail) = await PrepareAsync(run, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            ending = PrepareEnding.Cancelled;
            Say(run, PrepareMessages.Cancelled);
        }
#pragma warning disable CA1031 // A watch that dies on a throw is a bundle watched forever, and a fault on a thread-pool thread nobody observes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ending = PrepareEnding.Failed;
            detail = ex.Message;
            Say(run, ex.Message);
        }

        lock (_gate)
        {
            _runs.Remove(run);
        }

        run.End(ending, detail);
        Ended?.Invoke(this, run);
    }

    private async Task<(PrepareEnding Ending, string Detail)> PrepareAsync(PrepareRun run, CancellationToken token)
    {
        VaultResult<MaterializeStart> start = await _steps.StartAsync(run.OrderId, token).ConfigureAwait(false);
        if (!start.Succeeded)
        {
            return Fail(run, start.Error!);
        }

        MaterializeStart begun = start.Value!.Value;

        // ⛔ Nothing to build means there is NO JOB, so there is nothing to poll. Polling anyway would
        // sit on "waiting for the platform to pick this up" for the whole budget and end in a timeout,
        // for a bundle that was ready before the request was made. This is also the ONLY route from
        // the vault to an import for an already-complete bundle: Import refuses unless the cache
        // holds the zip, and nothing but a Prepare fills the cache.
        if (begun.Outcome == MaterializeStartOutcome.NothingToDo)
        {
            Say(run, PrepareMessages.NothingToDo(begun));
            return await DownloadAsync(run, token).ConfigureAwait(false);
        }

        Say(run, PrepareMessages.Began(begun));

        PollOutcome polled = await _steps.PollAsync(
            run.OrderId,
            MaterializeJobs.RequestedForPolling(begun),
            new Relay<MaterializeStatus>(status => Say(run, PrepareMessages.Progress(status, run.Label))),
            token).ConfigureAwait(false);

        if (polled.OutOfPolls)
        {
            Say(run, polled.Result.Error ?? string.Empty);
            return (PrepareEnding.StillPreparing, polled.Result.Error ?? string.Empty);
        }

        if (!polled.Result.Succeeded)
        {
            return Fail(run, polled.Result.Error!);
        }

        // A deliverable the platform will never produce for this area is a GAP, not a failure. Say so
        // and carry on: waiting for one is waiting forever.
        if (polled.Result.Value!.Value.Unproducible is { Count: > 0 } gaps)
        {
            Say(run, PrepareMessages.Gaps(gaps));
        }

        return await DownloadAsync(run, token).ConfigureAwait(false);
    }

    private async Task<(PrepareEnding Ending, string Detail)> DownloadAsync(PrepareRun run, CancellationToken token)
    {
        PreparedDownload download = await _steps
            .DownloadAsync(run.Bundle, new Relay<string>(line => Say(run, line)), token)
            .ConfigureAwait(false);

        run.Downloaded(download.Bundle);
        Say(run, download.Message);
        return download.Ready ? (PrepareEnding.Ready, string.Empty) : (PrepareEnding.Failed, download.Message);
    }

    private (PrepareEnding Ending, string Detail) Fail(PrepareRun run, string error)
    {
        Say(run, error);
        return (PrepareEnding.Failed, error);
    }

    private void Say(PrepareRun run, string message)
    {
        run.Say(message);
        Said?.Invoke(this, run);
    }

    /// <summary>
    /// Reports on the calling thread. <c>Progress&lt;T&gt;</c> would post to whatever context built
    /// it, and this is built on a thread-pool thread that has none — the hop to a window is its own.
    /// </summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
