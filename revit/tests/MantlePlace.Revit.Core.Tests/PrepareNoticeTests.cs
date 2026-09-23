using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// What the curator is told when a Prepare they stopped watching ends, and when they are told
/// nothing.
/// </summary>
/// <remarks>
/// The notice exists for a job nobody is looking at. So the vault being open silences it — the vault
/// already says what happened — and so does being signed out, because the curator chose that and a
/// failure it caused is not news. What is left is one notice per ending the curator would otherwise
/// have to reopen the vault to discover.
/// </remarks>
internal static class PrepareNoticeTests
{
    private const string Order = "order-1";
    private const string Label = "Harbour Point";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a bundle downloaded while the vault was closed is ready to import", () =>
        {
            PrepareNotice? notice = PrepareNotices.For(Order, Label, PrepareEnding.Ready, string.Empty, vaultOpen: false, signedIn: true);

            run.True(notice is not null, "a notice is given");
            run.Equal(notice!.Value.OrderId, Order, "it names the order it opens the vault on");
            run.Equal(notice.Value.Ending, PrepareEnding.Ready, "it carries the ending");
            run.Equal(notice.Value.Text, "Harbour Point is downloaded and ready to import.", "download, not build, is the promise");
        });

        run.Case("a failed Prepare says so, with the platform's reason", () =>
        {
            PrepareNotice? notice = PrepareNotices.For(
                Order, Label, PrepareEnding.Failed, "The platform could not build this bundle.", vaultOpen: false, signedIn: true);

            run.True(notice is not null, "a failure is news too — waiting on it is waiting forever");
            run.Equal(notice!.Value.Text, "Couldn't prepare Harbour Point. The platform could not build this bundle.", "the reason follows");
        });

        run.Case("a failure with no reason still names the bundle", () =>
        {
            PrepareNotice? notice = PrepareNotices.For(Order, Label, PrepareEnding.Failed, "  ", vaultOpen: false, signedIn: true);

            run.Equal(notice!.Value.Text, "Couldn't prepare Harbour Point.", "no dangling space, no empty reason");
        });

        run.Case("a Prepare that outlived its poll budget is still preparing, and says how to pick it up", () =>
        {
            PrepareNotice? notice = PrepareNotices.For(Order, Label, PrepareEnding.StillPreparing, "ignored", vaultOpen: false, signedIn: true);

            run.True(notice is not null, "the watch ending is news: nothing is watching any more");
            run.Contains(notice!.Value.Text, "Harbour Point is still being prepared.", "it is not a failure");
            run.Contains(notice.Value.Text, WindowLabels.PrepareForRevit, "it names the button that rejoins the job");
        });

        run.Case("a cancel the curator pressed is never announced back to them", () =>
        {
            run.True(PrepareNotices.For(Order, Label, PrepareEnding.Cancelled, string.Empty, vaultOpen: false, signedIn: true) is null, "cancelled");
        });

        run.Case("an interruption is never announced: nothing has ended, and the re-join tells the ending", () =>
        {
            run.True(PrepareNotices.For(Order, Label, PrepareEnding.Interrupted, "The platform is unavailable.", vaultOpen: false, signedIn: true) is null, "interrupted");
        });

        run.Case("while the vault is open, the vault is the notice", () =>
        {
            foreach (PrepareEnding ending in Enum.GetValues<PrepareEnding>())
            {
                run.True(
                    PrepareNotices.For(Order, Label, ending, "reason", vaultOpen: true, signedIn: true) is null,
                    $"{ending} with the vault open");
            }
        });

        run.Case("signed out: a failure is the sign-out's doing and is not announced", () =>
        {
            run.True(PrepareNotices.For(Order, Label, PrepareEnding.Failed, "Sign in to Mantle Place first.", vaultOpen: false, signedIn: false) is null, "failed");
            run.True(PrepareNotices.For(Order, Label, PrepareEnding.StillPreparing, string.Empty, vaultOpen: false, signedIn: false) is null, "still preparing");
        });

        run.Case("signed out after the download finished: the bundle is on disk and import needs no sign-in", () =>
        {
            run.True(PrepareNotices.For(Order, Label, PrepareEnding.Ready, string.Empty, vaultOpen: false, signedIn: false) is not null, "ready");
        });

        run.Case("a bundle with no label is named by its order", () =>
        {
            run.Equal(PrepareNotices.LabelOf("Harbour Point", Order), "Harbour Point", "the label when there is one");
            run.Equal(PrepareNotices.LabelOf("   ", Order), Order, "the order otherwise");
        });

        run.Case("pending notices: one per bundle, the latest ending wins", () =>
        {
            PendingNotices pending = new();
            PrepareNotice still = PrepareNotices.For(Order, Label, PrepareEnding.StillPreparing, string.Empty, false, true)!.Value;
            PrepareNotice ready = PrepareNotices.For(Order, Label, PrepareEnding.Ready, string.Empty, false, true)!.Value;
            PrepareNotice other = PrepareNotices.For("order-2", "Quay", PrepareEnding.Ready, string.Empty, false, true)!.Value;

            pending.Add(still);
            pending.Add(other);
            pending.Add(ready);

            run.Equal(pending.Count, 2, "two bundles, however many endings");
            run.Equal(pending.All[1].Text, ready.Text, "the later ending replaced the earlier, and moved to the end");

            pending.Clear();
            run.Equal(pending.Count, 0, "opening the vault clears them");
        });

        run.Case("the Vault badge counts, and stops counting at nine", () =>
        {
            run.True(VaultBadge.CountText(0) is null, "no badge at zero");
            run.Equal(VaultBadge.CountText(1), "1", "one");
            run.Equal(VaultBadge.CountText(9), "9", "nine");
            run.Equal(VaultBadge.CountText(12), "9+", "a digit is all a 32 px badge holds");
            run.True(VaultBadge.DrawsCount(32), "the large slot draws the count");
            run.False(VaultBadge.DrawsCount(16), "the small slot draws a dot");
        });

        run.Case("the Vault tooltip says what the badge means, and nothing when there is none", () =>
        {
            run.Equal(VaultBadge.ToolTipFor(0), VaultBadge.ToolTip, "the button's own tooltip");
            run.Equal(VaultBadge.ToolTipFor(1), "1 update in your vault. Open the vault to see it.", "singular");
            run.Equal(VaultBadge.ToolTipFor(3), "3 updates in your vault. Open the vault to see them.", "plural — a new order and a Prepare both count");
        });

        run.Case("notices stack upward from the owner's bottom-right corner", () =>
        {
            NoticeRect owner = new(100, 50, 1600, 900);

            NoticeRect first = NoticeStack.Place(owner, 0);
            run.Within(first.Left, 100 + 1600 - NoticeStack.Inset - NoticeStack.Width, 1e-9, "right-aligned inside the owner");
            run.Within(first.Top, 50 + 900 - NoticeStack.Inset - NoticeStack.Height, 1e-9, "on the owner's bottom edge");
            run.Within(first.Width, NoticeStack.Width, 1e-9, "fixed width");

            NoticeRect second = NoticeStack.Place(owner, 1);
            run.Within(second.Top, first.Top - NoticeStack.Height - NoticeStack.Gap, 1e-9, "the next sits above, a gap apart");
            run.Within(second.Left, first.Left, 1e-9, "in the same column");
        });

        run.Case("an owner narrower than a notice keeps the notice on the owner's left edge", () =>
        {
            NoticeRect first = NoticeStack.Place(new NoticeRect(10, 10, 200, 400), 0);
            run.Within(first.Left, 10, 1e-9, "clamped, never off the owner to the left");
        });

        run.Case("the Prepare lines the vault shows", () =>
        {
            MaterializeStatus working = new(MaterializeState.Processing, MaterializeStatus.Indeterminate, "job", string.Empty);
            MaterializeStatus half = new(MaterializeState.Processing, 0.5, "job", "Building 3 deliverable(s)…");

            run.Equal(PrepareMessages.Progress(working, Label), "Preparing Harbour Point: Processing (working).", "indeterminate is not 0%");
            run.Equal(PrepareMessages.Progress(half, Label), "Preparing Harbour Point: Building 3 deliverable(s)… (50%).", "the platform's own sentence");
            run.Equal(
                PrepareMessages.NothingToDo(MaterializeStart.NothingToDo(MaterializeJobs.RevitTokens)),
                "Everything Revit needs is already built. Fetching it…",
                "nothing missing");
            run.Contains(
                PrepareMessages.NothingToDo(MaterializeStart.NothingToDo([])),
                $"{MaterializeJobs.RevitTokens.Count} of the Revit deliverables aren't in this bundle",
                "the shortfall, without inventing a cause");
            run.Equal(
                PrepareMessages.Gaps([new MissingDeliverable("imagery", "")]),
                "Not available for this area: imagery.",
                "no reason is left out rather than guessed");
        });

        return run.Report("PrepareNotices");
    }
}
