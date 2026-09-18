namespace MantlePlace.Revit.Core;

/// <summary>What the attribution step does to the text note in the attribution view.</summary>
public enum AttributionNoteAction
{
    /// <summary>The manifest named no sources, so there is no text to write and no view to make.</summary>
    NothingToWrite,

    /// <summary>A note in the view already says exactly this. Nothing changes.</summary>
    Keep,

    /// <summary>The note an earlier build wrote is rewritten in place with this build's text.</summary>
    Rewrite,

    /// <summary>No note in the view is this plugin's, so a new one is created.</summary>
    Add,
}

/// <summary>The decision, and which of the view's existing notes it is about.</summary>
/// <param name="Action">What to do.</param>
/// <param name="NoteIndex">
/// The position in the list handed to <see cref="AttributionView.Decide"/> of the note kept or
/// rewritten; <c>-1</c> for the other two actions.
/// </param>
public readonly record struct AttributionNotePlan(AttributionNoteAction Action, int NoteIndex = -1);

/// <summary>
/// The "Mantle Place Attribution" drafting view: its name, the text its note carries, and which note
/// in it is this plugin's. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The view is found by <see cref="ViewName"/>, which is its identity: a drafting view has no
/// Comments parameter to stamp, and a name is what a curator sees in the project browser and places
/// on a sheet.
/// </para>
/// <para>
/// The note inside it has no identity slot either, so it is recognised by what it says. The note
/// this plugin wrote last time is the one whose text is the text the project's stored
/// <see cref="ProjectProvenance"/> produces — the record says which sources went in, and
/// <see cref="NoteText"/> turns them into the same words again. That note is rewritten when the
/// build changes; any other note in the view is a curator's and is left alone. A curator who edits
/// the plugin's note has made it theirs, and the next import adds a fresh one beside it rather than
/// overwriting their words.
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

    /// <summary>What to do with the view's notes.</summary>
    /// <param name="existingNoteTexts">The text of every note already in the view, in any order.</param>
    /// <param name="previousText">
    /// The text the project's stored provenance produces, or <c>null</c> when the project holds no
    /// record — a first import, or one whose record could not be read.
    /// </param>
    /// <param name="wantedText">This import's <see cref="NoteText"/>.</param>
    public static AttributionNotePlan Decide(
        IReadOnlyList<string> existingNoteTexts,
        string? previousText,
        string wantedText)
    {
        ArgumentNullException.ThrowIfNull(existingNoteTexts);
        ArgumentNullException.ThrowIfNull(wantedText);

        string wanted = Normalise(wantedText);
        if (wanted.Length == 0)
        {
            return new AttributionNotePlan(AttributionNoteAction.NothingToWrite);
        }

        int same = IndexOf(existingNoteTexts, wanted);
        if (same >= 0)
        {
            return new AttributionNotePlan(AttributionNoteAction.Keep, same);
        }

        string previous = Normalise(previousText ?? string.Empty);
        int ours = previous.Length == 0 ? -1 : IndexOf(existingNoteTexts, previous);
        return ours >= 0
            ? new AttributionNotePlan(AttributionNoteAction.Rewrite, ours)
            : new AttributionNotePlan(AttributionNoteAction.Add);
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
