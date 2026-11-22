using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class FeedbackAskedStore
{
    private readonly MachineRecordFile _file;

    public FeedbackAskedStore(string path) => _file = new MachineRecordFile(path);

    public static string DefaultPath => MachineRecordFile.InLocalAppData("feedback-asked.json");

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
