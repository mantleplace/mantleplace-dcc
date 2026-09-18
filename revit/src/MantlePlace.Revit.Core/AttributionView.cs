namespace MantlePlace.Revit.Core;

/// <summary>What the project already has under <see cref="AttributionView.ViewName"/>.</summary>
public enum AttributionViewFound
{
    /// <summary>No view by that name.</summary>
    None,

    /// <summary>A drafting view by that name — the one an earlier import made, or one made the same way.</summary>
    DraftingView,

    /// <summary>
    /// A view by that name that is not a drafting view this plugin can write into: a view template,
    /// or a plan, section or legend someone named that way.
    /// </summary>
    NotADraftingView,
}

/// <summary>What the attribution step does to the attribution view.</summary>
public enum AttributionNoteAction
{
    /// <summary>The manifest named no sources, so there is no text to write and no view to make.</summary>
    NothingToWrite,

    /// <summary>
    /// The name is taken by a view this plugin cannot write into, so nothing is written — and no
    /// second view is made, because Revit refuses a name already in use.
    /// </summary>
    ViewNameTaken,

    /// <summary>A note in the view already says exactly this. Nothing changes.</summary>
    Keep,

    /// <summary>The note this order's earlier build wrote is rewritten in place with this build's text.</summary>
    Rewrite,

    /// <summary>No note in the view is this order's, so a new one is created.</summary>
    Add,
}

/// <summary>What the project's stored record says the plugin last wrote into the view.</summary>
/// <param name="OrderId">The order the record belongs to.</param>
/// <param name="NoteText">The note's text exactly as it was written, or empty when none was.</param>
public sealed record RecordedAttribution(string OrderId, string NoteText);

/// <summary>The whole decision, for the shim to carry out.</summary>
/// <param name="Action">What to do.</param>
/// <param name="NoteIndex">
/// For <see cref="AttributionNoteAction.Keep"/> and <see cref="AttributionNoteAction.Rewrite"/>, the
/// position of that note in the list handed to <see cref="AttributionView.Decide"/>; <c>-1</c> otherwise.
/// </param>
/// <param name="CreateView">Whether the view has to be made first — an add with no view to add to.</param>
/// <param name="TextToRecord">
/// What the stored record says the plugin's note reads once this step is done — the text the next
/// import looks for.
/// </param>
public readonly record struct AttributionNotePlan(
    AttributionNoteAction Action,
    int NoteIndex,
    bool CreateView,
    string TextToRecord);

/// <summary>
/// The "Mantle Place Attribution" drafting view: its name, the text its note carries, and which note
/// in it is this order's. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The text is the manifest's own words, applied verbatim (<c>HPS-33</c>): this host makes no
/// licensing decision and writes what <c>attribution.sources[]</c> says. The two things
/// <see cref="Line"/> does to those words are layout, not content.
/// </para>
/// <para>
/// The view is found by <see cref="ViewName"/>, which is its identity: a drafting view has no
/// Comments parameter to stamp, and a name is what a curator sees in the project browser and places
/// on a sheet.
/// </para>
/// <para>
/// The note inside it has no identity slot either, so it is recognised by what it says. The project's
/// stored record (<see cref="ProvenanceStorage"/>) holds the order it belongs to and the exact text
/// the plugin wrote; the note carrying that text is this order's, and it is rewritten when the build
/// changes. Any other note is somebody else's — a curator's, or another order's in a project holding
/// two — and is left alone. A curator who edits the plugin's note has made it theirs, and the next
/// import adds a fresh one beside it rather than overwriting their words. Why a text match rather
/// than a stamp, and why the record is permanent, is ADR 0011.
/// </para>
/// </remarks>
public static class AttributionView
{
    /// <summary>The drafting view's name, and the identity a re-import finds it by.</summary>
    public const string ViewName = "Mantle Place Attribution";

    /// <summary>
    /// Revit's paragraph separator inside a text note. <c>TextNote.Text</c> hands paragraphs back
    /// split by a carriage return, whatever they were written with.
    /// </summary>
    public const char LineBreak = '\r';

    /// <summary>Between the fields of one source's line.</summary>
    private const string FieldSeparator = " — ";

    /// <summary>
    /// One line per source, in the manifest's order. Empty when there are no sources.
    /// </summary>
    public static string NoteText(IReadOnlyList<AttributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return string.Join(LineBreak, sources.Select(Line));
    }

    /// <summary>
    /// One source's line: its attribution text, its licence text and its licence URL, each verbatim,
    /// each left out when the manifest gave none.
    /// </summary>
    /// <remarks>
    /// Two things are done to the manifest's words, and neither changes what they say. A line break
    /// inside a field becomes a space, so one source is always one line. And a source with no
    /// attribution text is named by its <c>provider_id</c>, the only name the manifest gave it —
    /// dropping it would leave a licence in the view with nothing saying whose it is.
    /// </remarks>
    public static string Line(AttributionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string? name = source.AttributionText ?? (source.ProviderId.Length > 0 ? source.ProviderId : null);
        string?[] fields = [name, source.License, source.LicenseUrl];

        return string.Join(
            FieldSeparator,
            fields.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => OneLine(field!)));
    }

    /// <summary>What to do with the view and its notes.</summary>
    /// <param name="found">What the project holds under <see cref="ViewName"/>.</param>
    /// <param name="existingNoteTexts">
    /// The text of every note in that view when it is a <see cref="AttributionViewFound.DraftingView"/>,
    /// in any order; ignored otherwise.
    /// </param>
    /// <param name="recorded">
    /// The project's stored record, or <c>null</c> when it holds none — a first import, or one whose
    /// record could not be read.
    /// </param>
    /// <param name="orderId">The order being imported.</param>
    /// <param name="wantedText">This import's <see cref="NoteText"/>.</param>
    public static AttributionNotePlan Decide(
        AttributionViewFound found,
        IReadOnlyList<string> existingNoteTexts,
        RecordedAttribution? recorded,
        string orderId,
        string wantedText)
    {
        ArgumentNullException.ThrowIfNull(existingNoteTexts);
        ArgumentNullException.ThrowIfNull(orderId);
        ArgumentNullException.ThrowIfNull(wantedText);

        // Only this order's record identifies a note as ours. Another order's note is that order's
        // credits, for ground that is still in the project.
        string previous = recorded is not null && string.Equals(recorded.OrderId, orderId, StringComparison.Ordinal)
            ? Normalise(recorded.NoteText)
            : string.Empty;
        string wanted = Normalise(wantedText);

        // Where nothing is written, the record keeps pointing at this order's note, so a later build
        // that does name sources still finds it.
        if (wanted.Length == 0)
        {
            return new AttributionNotePlan(AttributionNoteAction.NothingToWrite, -1, false, previous);
        }

        if (found == AttributionViewFound.NotADraftingView)
        {
            return new AttributionNotePlan(AttributionNoteAction.ViewNameTaken, -1, false, previous);
        }

        IReadOnlyList<string> notes = found == AttributionViewFound.DraftingView ? existingNoteTexts : [];

        int same = IndexOf(notes, wanted);
        if (same >= 0)
        {
            return new AttributionNotePlan(AttributionNoteAction.Keep, same, false, wanted);
        }

        int ours = previous.Length == 0 ? -1 : IndexOf(notes, previous);
        return ours >= 0
            ? new AttributionNotePlan(AttributionNoteAction.Rewrite, ours, false, wanted)
            : new AttributionNotePlan(AttributionNoteAction.Add, -1, found == AttributionViewFound.None, wanted);
    }

    private static int IndexOf(IReadOnlyList<string> texts, string normalised)
    {
        for (int index = 0; index < texts.Count; index++)
        {
            if (string.Equals(Normalise(texts[index] ?? string.Empty), normalised, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// A note's text as it compares: every line break read as Revit's, and the trailing break Revit
    /// appends to a note's text dropped.
    /// </summary>
    private static string Normalise(string text)
        => text.Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', LineBreak)
            .TrimEnd();

    private static string OneLine(string field)
        => string.Join(' ', field.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
