using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless vault browser: list, materialize, download, import.
/// </summary>
/// <remarks>
/// <para>
/// Modeless on purpose. A materialize can take ten minutes, and a modal dialog would hold Revit's
/// message loop for all of it. The curator keeps working; the window updates when the job does.
/// </para>
/// <para>
/// <b>Closing this window is not cancelling.</b> Only the Cancel button cancels. Closing detaches
/// the view: the ETL job keeps running server-side, and reopening the browser rejoins it through
/// <c>HPS-24</c>'s single-flight response rather than queueing a second job. A window's close box is
/// "I am done looking", not "throw away the thing I paid for".
/// </para>
/// <para>
/// Built in code rather than XAML: it is one list and five buttons, and a code-only window has no
/// build-action, resource-lookup or designer surface to go wrong inside a Revit add-in.
/// </para>
/// </remarks>
internal sealed class VaultBrowserWindow : Window
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;
    private readonly ExternalEvent _importEvent;
    private readonly BundleImportEventHandler _importHandler;

    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly Button _refresh = new() { Content = "Refresh", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _prepare = new() { Content = "Prepare for Revit", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _import = new() { Content = "Import", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _remove = new() { Content = "Remove download", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };

    private List<VaultBundle> _bundles = [];
    private CancellationTokenSource? _work;

    internal VaultBrowserWindow(
        AuthSession session,
        VaultClient vault,
        BundleCache cache,
        ExternalEvent importEvent,
        BundleImportEventHandler importHandler,
        IntPtr revitWindow)
    {
        _session = session;
        _vault = vault;
        _cache = cache;
        _importEvent = importEvent;
        _importHandler = importHandler;

        Title = "Mantle Place — your vault";
        Width = 720;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by Revit's main window so it stays in front of it and minimises with it, rather
        // than becoming a stray top-level window the curator loses behind the model.
        new WindowInteropHelper(this) { Owner = revitWindow };

        Content = BuildLayout();

        _refresh.Click += (_, _) => _ = RefreshAsync();
        _prepare.Click += (_, _) => _ = PrepareAsync();
        _import.Click += (_, _) => _ = ImportAsync();
        _remove.Click += (_, _) => RemoveSelected();
        _cancel.Click += (_, _) => _work?.Cancel();

        _importHandler.Completed += OnImportCompleted;
        Closed += (_, _) => _importHandler.Completed -= OnImportCompleted;

        Loaded += (_, _) => _ = RefreshAsync();
    }

    private UIElement BuildLayout()
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_prepare);
        buttons.Children.Add(_import);
        buttons.Children.Add(_remove);
        buttons.Children.Add(_cancel);

        DockPanel root = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(_list);
        return root;
    }

    private VaultBundle? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _bundles.Count
        ? _bundles[_list.SelectedIndex]
        : null;

    private async Task RefreshAsync()
    {
        if (!await BeginAsync("Loading your vault…").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            (VaultListing? listing, string? error) = await _vault.ListAsync(_work!.Token).ConfigureAwait(true);

            if (error is not null)
            {
                Report(error);
                return;
            }

            _bundles = [.. listing!.Bundles];
            _list.Items.Clear();
            foreach (VaultBundle bundle in _bundles)
            {
                _list.Items.Add(Describe(bundle));
            }

            // ⛔HPS-21: rows that were skipped are SAID, not swallowed. A silent skip hides platform
            // corruption for as long as it lasts.
            Report(listing.Warnings.Count == 0
                ? $"{_bundles.Count} bundle(s)."
                : $"{_bundles.Count} bundle(s). {listing.Warnings.Count} row(s) were unreadable and skipped.");
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// Materialize → poll → <b>re-list</b> → download.
    /// </summary>
    /// <remarks>
    /// The re-list is not optional (<c>HPS-18</c>): it is where the integrity facts for the
    /// freshly built bundle come from. Downloading against the pre-materialize row would mean
    /// checking the new zip against a size and digest nobody had yet.
    /// </remarks>
    private async Task PrepareAsync()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        if (!await BeginAsync($"Preparing {bundle.AoiLabel}…").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            CancellationToken token = _work!.Token;

            VaultResult<MaterializeStart> start = await _vault
                .StartMaterializeAsync(bundle.OrderId, MaterializeJobs.HostScope, token)
                .ConfigureAwait(true);

            if (!start.Succeeded)
            {
                Report(start.Error!);
                return;
            }

            MaterializeStart begun = start.Value!.Value;

            // ⛔ Nothing to build means there is NO JOB, so there is nothing to poll. Polling anyway
            // would sit on "waiting for the platform to pick this up" for the whole budget and end in
            // a timeout, for a bundle that was ready before the request was made. This is also the
            // ONLY route from the vault to an import for an already-complete bundle: Import refuses
            // unless the cache holds the zip, and nothing but this method fills the cache.
            if (begun.Outcome == MaterializeStartOutcome.NothingToDo)
            {
                Report(DescribeNothingToDo(begun));
                await DownloadPreparedAsync(bundle, token).ConfigureAwait(true);
                return;
            }

            switch (begun.Outcome)
            {
                case MaterializeStartOutcome.Joined:
                    Report("This bundle was already being prepared — following that job rather than starting a second.");
                    break;

                case MaterializeStartOutcome.Queued:
                    Report("Your order is still being built. Your Revit deliverables are queued and "
                        + "start on their own as soon as it finishes.");
                    break;

                default:
                    Report("Preparing your Revit deliverables…");
                    break;
            }

            IReadOnlyList<string> requested = MaterializeJobs.RequestedForPolling(begun);

            Progress<MaterializeStatus> progress = new(status => Report(Describe(status, bundle)));
            VaultResult<MaterializeStatus> finished = await _vault
                .PollToCompletionAsync(bundle.OrderId, requested, progress, token)
                .ConfigureAwait(true);

            if (!finished.Succeeded)
            {
                Report(finished.Error!);
                return;
            }

            // A deliverable the platform will never produce for this area is a GAP, not a failure.
            // Say so and carry on: waiting for one is waiting forever.
            if (finished.Value!.Value.Unproducible is { Count: > 0 } gaps)
            {
                Report(DescribeGaps(gaps));
            }

            await DownloadPreparedAsync(bundle, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled. Nothing was left half-downloaded.");
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// Re-list, then download (<c>HPS-18</c>).
    /// </summary>
    /// <remarks>
    /// Shared by the polled path and the nothing-to-build path so there is one copy of the re-list.
    /// It is not optional on either: it is where the integrity facts for the bundle as it stands
    /// come from, and checking a download against a pre-materialize size and digest is checking it
    /// against numbers that describe a different file.
    /// </remarks>
    private async Task DownloadPreparedAsync(VaultBundle bundle, CancellationToken token)
    {
        Report("Fetching the integrity details…");
        (VaultListing? relisted, string? listError) = await _vault.ListAsync(token).ConfigureAwait(true);
        if (listError is not null)
        {
            Report(listError);
            return;
        }

        VaultBundle refreshed = relisted!.Bundles
            .FirstOrDefault(row => string.Equals(row.OrderId, bundle.OrderId, StringComparison.Ordinal))
            ?? bundle;

        Report("Downloading…");
        string? downloadError = await _vault.DownloadAsync(refreshed, _cache, token).ConfigureAwait(true);

        Report(downloadError ?? _cache
            .Inspect(refreshed.OrderId, refreshed.SizeBytes, refreshed.Sha256, refreshed.ManifestVersion)
            .Describe());
    }

    /// <summary>
    /// What to say when the platform had nothing to build.
    /// </summary>
    /// <remarks>
    /// The response names what IS delivered and gives no reasons for the rest, so this states the
    /// shortfall without inventing a cause for it. The per-artifact reason arrives at import time
    /// from <c>hosts.revit.readiness</c>, which is the field that actually knows.
    /// </remarks>
    private static string DescribeNothingToDo(MaterializeStart start)
    {
        HashSet<string> delivered = new(start.Tokens, StringComparer.Ordinal);
        int absent = MaterializeJobs.RevitTokens.Count(token => !delivered.Contains(token));

        return absent == 0
            ? "Everything Revit needs is already built. Fetching it…"
            : $"Everything available for this area is already built. {absent} of the Revit "
                + "deliverables aren't in this bundle; the import will say why. Fetching it…";
    }

    /// <summary>The platform's own reason for each deliverable it will never produce here.</summary>
    private static string DescribeGaps(IReadOnlyList<MissingDeliverable> gaps)
    {
        IEnumerable<string> clauses = gaps.Select(gap =>
            ReadinessReasons.ClauseFor(gap.Reason) is { } clause
                ? $"{gap.Token} ({clause})"
                : gap.Token);

        return $"Not available for this area: {string.Join("; ", clauses)}.";
    }

    private async Task ImportAsync()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        CacheEntry entry = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion);
        if (entry.State != CacheState.CachedValid)
        {
            Report(entry.Describe() + " Use “Prepare for Revit” first.");
            return;
        }

        Report("Importing into the active project…");

        // Hands off to Revit's thread. The window stays responsive and hears back through Completed.
        _importHandler.QueueImport(entry.Layout.BundleZipPath);
        _importEvent.Raise();

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void RemoveSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        // HPS-44: eviction is explicit and per-order, and this button is the only thing that does
        // it. Nothing in this plugin reclaims disk on the curator's behalf.
        _cache.Remove(bundle.OrderId);
        Report($"Removed the local copy of {bundle.AoiLabel}. It stays in your vault and can be downloaded again.");
    }

    private void OnImportCompleted(object? sender, string report) => Report(report);

    private async Task<bool> BeginAsync(string message)
    {
        if (_work is not null)
        {
            Report("Already busy — wait for the current step, or cancel it.");
            return false;
        }

        if (_session.State != AuthState.Authenticated)
        {
            Report("Sign in first: Mantle Place ▸ Account ▸ Sign in.");
            return false;
        }

        if (_session.IsAccessTokenExpired(DateTimeOffset.UtcNow))
        {
            Report("Renewing your sign-in…");
            AuthOutcome renewed = await _session.RefreshAsync().ConfigureAwait(true);
            if (!renewed.Succeeded)
            {
                Report(renewed.Message.Length > 0 ? renewed.Message : "Your sign-in expired. Sign in again.");
                return false;
            }
        }

        _work = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        Report(message);
        return true;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        _cancel.IsEnabled = false;
    }

    private void Report(string message) => _status.Text = message;

    private string Describe(VaultBundle bundle)
    {
        CacheEntry entry = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion);
        string area = bundle.AreaKm2 is { } km2
            ? km2.ToString("0.##", CultureInfo.InvariantCulture) + " km²"
            : "area unknown";

        string label = bundle.AoiLabel.Length > 0 ? bundle.AoiLabel : bundle.OrderId;
        return $"{label} — {area} — {bundle.Status} — {entry.Describe()}";
    }

    private static string Describe(MaterializeStatus status, VaultBundle bundle)
    {
        // Indeterminate is NOT zero. A progress bar sitting at 0% and a spinner say different
        // things to a curator deciding whether to wait.
        string progress = status.Fraction < 0
            ? "working"
            : (status.Fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        // The platform's own sentence when it gave one — "Building 3 deliverable(s)…" beats the bare
        // state name, and on Unknown it is the difference between a spinner and an explanation.
        return status.Message.Length > 0
            ? $"Preparing {bundle.AoiLabel}: {status.Message} ({progress})."
            : $"Preparing {bundle.AoiLabel}: {status.State} ({progress}).";
    }
}
