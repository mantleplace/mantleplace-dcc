namespace MantlePlace.Core;

public enum AttributionViewFound
{
    None,

    DraftingView,

    NotADraftingView,
}

public enum AttributionNoteAction
{
    NothingToWrite,

    ViewNameTaken,

    Keep,

    Rewrite,

    Add,
}

public sealed record RecordedAttribution(string OrderId, string NoteText)
{
    public string JobId { get; init; } = string.Empty;
}

public readonly record struct AttributionNotePlan(
    AttributionNoteAction Action,
    int NoteIndex,
    bool CreateView,
    string TextToRecord);

public static class AttributionView
{
    public const string ViewName = "Mantle Place Attribution";

    public const char LineBreak = '\r';

    private const string FieldSeparator = " — ";

    public static string NoteText(IReadOnlyList<AttributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return string.Join(LineBreak, sources.Select(Line));
    }

    public static string Line(AttributionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string? name = source.AttributionText ?? (source.ProviderId.Length > 0 ? source.ProviderId : null);
        string?[] fields = [name, source.License, source.LicenseUrl];

        return string.Join(
            FieldSeparator,
            fields.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => OneLine(field!)));
    }

    public static AttributionNotePlan Decide(
        AttributionViewFound found,
        IReadOnlyList<string> existingNoteTexts,
        RecordedAttribution? recorded,
        string orderId,
        string jobId,
        string wantedText)
    {
        ArgumentNullException.ThrowIfNull(existingNoteTexts);
        ArgumentNullException.ThrowIfNull(orderId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(wantedText);

        string previous = recorded is { } record && RecordsThisBundle(record, orderId, jobId)
            ? Normalise(record.NoteText)
            : string.Empty;
        string wanted = Normalise(wantedText);

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

    private static bool RecordsThisBundle(RecordedAttribution record, string orderId, string jobId)
        => orderId.Length > 0
            ? string.Equals(record.OrderId, orderId, StringComparison.Ordinal)
            : jobId.Length > 0 && string.Equals(record.JobId, jobId, StringComparison.Ordinal);

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

    private static string Normalise(string text)
        => text.Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', LineBreak)
            .TrimEnd();

    private static string OneLine(string field)
        => string.Join(' ', field.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
