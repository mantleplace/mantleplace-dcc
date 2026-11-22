namespace MantlePlace.Core;

public readonly record struct ImportChunk(int Start, int Count);

public static class ImportChunking
{
    public const int ElementsPerTransaction = 200;

    public static IReadOnlyList<ImportChunk> Chunks(int count)
    {
        List<ImportChunk> chunks = [];
        for (int start = 0; start < count; start += ElementsPerTransaction)
        {
            chunks.Add(new ImportChunk(start, Math.Min(ElementsPerTransaction, count - start)));
        }

        return chunks;
    }
}
