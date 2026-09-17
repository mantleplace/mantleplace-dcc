namespace MantlePlace.Revit.Core;

/// <summary>
/// Decides whether an exception that reached the host's UI dispatcher came out of this plugin.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the judgement behind the shim's dispatcher handler, kept here so it can be
/// asserted without launching Revit — the handler itself cannot be, and neither can the crash it
/// exists to prevent.
/// </para>
/// <para>
/// <b>The question it answers is deliberately narrow.</b> A dispatcher handler that swallows
/// everything is worse than the crash it hides: the dispatcher belongs to the host, and marking a
/// fault of the host's own as handled leaves an application running on state it already decided it
/// could not trust. So the rule is ownership, not severity — if no frame on the way down is ours,
/// the answer is no and the host gets its exception exactly as it would have without us.
/// </para>
/// <para>
/// This is a net under a mistake, not a licence to throw from an event handler. A handler that can
/// throw is a defect wherever it lives; the net only decides who pays for it.
/// </para>
/// </remarks>
public static class AddinFaults
{
    /// <summary>
    /// The namespace root every assembly in this plugin shares, with its trailing dot.
    /// </summary>
    /// <remarks>
    /// The dot is not decoration. Without it this matches a type in some unrelated
    /// <c>MantlePlaceSomething</c> namespace that is nothing to do with us, and the one thing this
    /// predicate must never do is claim a fault it did not cause.
    /// </remarks>
    private const string OwnNamespacePrefix = "MantlePlace.";

    /// <summary>
    /// True when at least one frame belongs to this plugin.
    /// </summary>
    /// <param name="declaringTypeNames">
    /// The full names of the declaring types on the exception's stack, innermost first. A frame with
    /// no declaring type — a lambda compiled into a native helper, a frame the runtime elided —
    /// arrives as null and is skipped rather than guessed at.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Any frame, not just the innermost.</b> The throw that prompted this was an
    /// <c>ObjectDisposedException</c> raised inside <c>CancellationTokenSource.Cancel</c> — a
    /// framework type — from a handler of ours two frames up. Judging on the innermost frame alone
    /// would have declined exactly the fault it was written for, because a plugin rarely throws its
    /// own exception type; it usually mis-drives someone else's.
    /// </para>
    /// <para>
    /// The cost of that breadth is a false claim when the host calls into us and we call back into
    /// the host, which then throws: our frame is on the stack, so we take a fault that is arguably
    /// the host's. That trade is deliberate — the alternative errs toward terminating Revit, and
    /// between the two, the one that keeps a curator's session is the right default.
    /// </para>
    /// </remarks>
    public static bool IsOurs(IReadOnlyList<string?> declaringTypeNames)
    {
        ArgumentNullException.ThrowIfNull(declaringTypeNames);

        for (int i = 0; i < declaringTypeNames.Count; i++)
        {
            string? name = declaringTypeNames[i];

            if (name is not null && name.StartsWith(OwnNamespacePrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
