using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// This machine's record of the bundles the import prompt has already asked about
/// (<see cref="AskedBundles"/>), on disk, shared by every Revit process on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per machine, not per Revit version</b> (<see cref="MachineRecordFile"/>): a bundle
/// imported in Revit 2025 and again in 2027 is asked about once.
/// </para>
/// <para>
/// ⚠ <b>Nothing here can fail an import, and nothing here asks twice.</b> A file that is missing or
/// damaged is no memory, and the question is asked and the file started over. A file that is locked or
/// refused cannot keep the answer, so <see cref="Claim"/> asks nothing: a missed question costs a
/// curator nothing, a repeated one is the nuisance the once-per-bundle rule exists to prevent.
/// </para>
/// </remarks>
public sealed class FeedbackAskedStore
{
    private readonly MachineRecordFile _file;

    public FeedbackAskedStore(string path) => _file = new MachineRecordFile(path);

    /// <summary>Where the record lives: beside the bundle cache, under the curator's local app data.</summary>
    public static string DefaultPath => MachineRecordFile.InLocalAppData("feedback-asked.json");

    /// <summary>
    /// Decides whether this import asks (<see cref="ImportFeedbackPrompt.Ask"/>) and, if so, remembers the
    /// bundle in the same exclusive open, so two Revits finishing the same bundle ask once between them.
    /// </summary>
    /// <returns>The order id to ask about, or <c>null</c> when nothing is asked.</returns>
    public string? Claim(ImportEnding ending, string? orderId)
    {
        try
        {
            return _file.Change<string?>(bytes =>
            {
                AskedBundles asked = AskedBundles.Parse(bytes) ?? AskedBundles.Empty;
                string? order = ImportFeedbackPrompt.Ask(ending, orderId, asked);
                return order is null ? (bytes, null) : (asked.With(order).ToBytes(), order);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
