using MantlePlace.Core;

namespace MantlePlace.Client;

public readonly record struct PreparedDownload(VaultBundle Bundle, bool Ready, string Message);

public interface IPrepareSteps
{
    Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken);

    Task<PollOutcome> PollAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        IProgress<MaterializeStatus> progress,
        CancellationToken cancellationToken);

    Task<PreparedDownload> DownloadAsync(VaultBundle bundle, IProgress<string> say, CancellationToken cancellationToken);
}

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

public sealed class PrepareRun
{
    private readonly CancellationTokenSource _cancel = new();
    private volatile string _lastMessage;
    private volatile bool _interrupted;

    internal PrepareRun(VaultBundle bundle, DateTimeOffset startedAt, bool rejoined)
    {
        Bundle = bundle;
        OrderId = bundle.OrderId;
        Label = PrepareNotices.LabelOf(bundle.AoiLabel, bundle.OrderId);
        StartedAt = startedAt;
        Rejoined = rejoined;
        _lastMessage = PrepareMessages.Starting(Label);
    }

    public string OrderId { get; }

    public DateTimeOffset StartedAt { get; }

    public bool Rejoined { get; }

    public string Label { get; }

    public VaultBundle Bundle { get; private set; }

    public string LastMessage => _lastMessage;

    public PrepareEnding? Ending { get; private set; }

    public string Detail { get; private set; } = string.Empty;

    public Task Completion { get; internal set; } = Task.CompletedTask;

    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal CancellationToken Token => _cancel.Token;

    internal bool WasInterrupted => _interrupted;

    internal void Interrupt()
    {
        _interrupted = true;
        Cancel();
    }

    internal void Say(string message) => _lastMessage = message;

    internal void Downloaded(VaultBundle bundle) => Bundle = bundle;

    internal void End(PrepareEnding ending, string detail)
    {
        Ending = ending;
        Detail = detail;
        _cancel.Dispose();
    }
}

public sealed class PrepareWatcher
{
    private readonly IPrepareSteps _steps;
    private readonly IPrepareLedger _ledger;
    private readonly object _gate = new();
    private readonly List<PrepareRun> _runs = [];

    public PrepareWatcher(IPrepareSteps steps)
        : this(steps, NoLedger.Instance)
    {
    }

    public PrepareWatcher(IPrepareSteps steps, IPrepareLedger ledger)
    {
        _steps = steps;
        _ledger = ledger;
    }

    public event EventHandler<PrepareRun>? Said;

    public event EventHandler<PrepareRun>? Ended;

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

    public PrepareRun? Watching(string orderId)
    {
        lock (_gate)
        {
            return Find(orderId);
        }
    }

    private PrepareRun? Find(string orderId)
        => _runs.Find(held => string.Equals(held.OrderId, orderId, StringComparison.Ordinal));

    public PrepareRun Prepare(VaultBundle bundle, out bool joined)
        => Watch(bundle, DateTimeOffset.UtcNow, rejoined: false, out joined);

    public PrepareRun Rejoin(VaultBundle bundle, DateTimeOffset startedAt)
        => Watch(bundle, startedAt, rejoined: true, out _);

    private PrepareRun Watch(VaultBundle bundle, DateTimeOffset startedAt, bool rejoined, out bool joined)
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

            run = new PrepareRun(bundle, startedAt, rejoined);
            _runs.Add(run);
        }

        joined = false;
        Said?.Invoke(this, run);
        run.Completion = Task.Run(() => WatchAsync(run));
        return run;
    }

    public void InterruptAll()
    {
        foreach (PrepareRun run in Runs)
        {
            run.Interrupt();
        }
    }

    private async Task WatchAsync(PrepareRun run)
    {
        PrepareEnding ending;
        string detail = string.Empty;

        Record(() => _ledger.Started(run.OrderId, run.StartedAt));

        try
        {
            (ending, detail) = await PrepareAsync(run, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            ending = PrepareEnding.Cancelled;
            Say(run, PrepareMessages.Cancelled);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ending = PrepareEnding.Failed;
            detail = ex.Message;
            Say(run, ex.Message);
        }

        if (run.WasInterrupted)
        {
            ending = PrepareEnding.Interrupted;
        }

        Record(() => _ledger.Ended(run.OrderId, ending));

        lock (_gate)
        {
            _runs.Remove(run);
        }

        run.End(ending, detail);
        Ended?.Invoke(this, run);
    }

    private static void Record(Action write)
    {
        try
        {
            write();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task<(PrepareEnding Ending, string Detail)> PrepareAsync(PrepareRun run, CancellationToken token)
    {
        VaultResult<MaterializeStart> start = await _steps.StartAsync(run.OrderId, token).ConfigureAwait(false);
        if (!start.Succeeded)
        {
            if (run.Rejoined)
            {
                Say(run, start.Error!);
                return (PrepareEnding.Interrupted, start.Error!);
            }

            return Fail(run, start.Error!);
        }

        MaterializeStart begun = start.Value!.Value;

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

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class NoLedger : IPrepareLedger
    {
        internal static readonly NoLedger Instance = new();

        public void Started(string orderId, DateTimeOffset startedAt)
        {
        }

        public void Ended(string orderId, PrepareEnding ending)
        {
        }
    }
}
